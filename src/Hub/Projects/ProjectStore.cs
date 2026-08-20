using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Projects;

/// <summary>Where <see cref="ProjectStore"/> keeps its state and project icons.</summary>
public sealed class ProjectsOptions
{
    public const string Section = "Projects";

    /// <summary>Full path to the project state JSON file. Empty picks the same <c>$HOME/data</c> convention as the operator state.</summary>
    public string StatePath { get; set; } = string.Empty;

    /// <summary>Directory project icons are stored under. Empty picks a sibling of the state file.</summary>
    public string IconRoot { get; set; } = string.Empty;
}

/// <summary>One project, as persisted. See <see cref="ProjectInfo"/> for the wire shape this projects onto.</summary>
public sealed class ProjectRecord
{
    public string ProjectId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? SiteUrl { get; set; }

    public string? RepoUrl { get; set; }

    /// <summary>True only for the reserved, non-deletable General project.</summary>
    public bool IsGeneral { get; set; }

    /// <summary>Zero means no custom icon has ever been uploaded.</summary>
    public int IconVersion { get; set; }

    /// <summary>The content type the icon file on disk was uploaded as. Null when <see cref="IconVersion"/> is zero.</summary>
    public string? IconContentType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ProjectInfo ToInfo() => new()
    {
        ProjectId = ProjectId,
        Name = Name,
        Description = Description,
        SiteUrl = SiteUrl,
        RepoUrl = RepoUrl,
        IsGeneral = IsGeneral,
        IconVersion = IconVersion,
        CreatedAt = CreatedAt,
    };
}

/// <summary>A durable project choice for one live session.</summary>
public sealed class SessionProjectRecord
{
    public string MachineId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;
}

/// <summary>The whole store, on disk, in one JSON file. See <see cref="ProjectStore"/> for why.</summary>
public sealed class ProjectState
{
    /// <summary>Schema version, so a future shape change can be migrated rather than guessed at.</summary>
    public int Schema { get; set; } = 2;

    /// <summary>User key to that user's projects, General included.</summary>
    public Dictionary<string, List<ProjectRecord>> Projects { get; set; } = new(StringComparer.Ordinal);

    /// <summary>User key to project assignments for sessions that may be re-announced after a restart.</summary>
    public Dictionary<string, List<SessionProjectRecord>> SessionProjects { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>
/// Per-user project definitions: the hub's second piece of durable state, after
/// <c>OperatorStateStore</c>, and built on exactly the same assumptions — see that
/// type for why a file and not a database, and what it assumes about running as one
/// instance.
/// <para>
/// <b>Unlike the operator state, every mutation flushes immediately</b> rather than
/// waiting for a timer. Project CRUD is rare and user-initiated — nothing here sits
/// on a hot path — so there is no cost to paying for durability up front, and a
/// silently lost create or delete would be a materially worse experience than a
/// silently lost usage counter.
/// </para>
/// <para>
/// <b>The General project is lazily seeded and never deleted or renamed.</b>
/// Every partition access ensures it exists first, so a user who has never touched
/// projects still has exactly one - the fixed catch-all new sessions default to.
/// Its optional metadata and icon remain editable.
/// </para>
/// <para>
/// <b>Icons are files next to the state file, not inside it.</b> A user's uploaded
/// icon can be a few hundred kilobytes; putting that in the JSON blob would multiply
/// the cost of every unrelated read and rewrite the whole file on every upload. They
/// are served through <c>Program.cs</c>'s <c>/projects/{id}/icon</c> endpoints, never
/// over SignalR.
/// </para>
/// </summary>
public sealed class ProjectStore
{
    /// <summary>The reserved id every user's General project has.</summary>
    public const string GeneralProjectId = "general";

    public const string GeneralProjectName = "General";

    private const int MaxNameLength = 60;
    private const int MaxDescriptionLength = 280;
    private const int MaxUrlLength = 2048;

    /// <summary>Kept well under SignalR's own payload ceiling and under what a phone upload should ever need.</summary>
    public const long MaxIconBytes = 512 * 1024;

    private static readonly HashSet<string> AllowedIconContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp" };
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<ProjectStore> _logger;
    private readonly ProjectState _state;

    private bool _dirty;
    private bool _persistenceAvailable = true;

    public ProjectStore(IOptions<ProjectsOptions> options, TimeProvider time, ILogger<ProjectStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _time = time;
        _logger = logger;
        StatePath = ResolvePath(options.Value.StatePath);
        IconRoot = ResolveIconRoot(options.Value.IconRoot, StatePath);
        _state = Load();
    }

    /// <summary>Where the state file lives. Exposed so a startup log can say where to look.</summary>
    public string StatePath { get; }

    /// <summary>Where icon files live. Exposed for the same reason.</summary>
    public string IconRoot { get; }

    /// <summary>Every project this user has, General first. Auto-seeds General on first access.</summary>
    public ProjectInfo[] List(string userKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_gate)
        {
            return PartitionOf(userKey)
                .OrderByDescending(project => project.IsGeneral)
                .ThenBy(project => project.CreatedAt)
                .Select(project => project.ToInfo())
                .ToArray();
        }
    }

    /// <summary>True when a project with this id belongs to this user (General included).</summary>
    public bool Exists(string userKey, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        lock (_gate)
        {
            return PartitionOf(userKey).Exists(project => project.ProjectId == projectId);
        }
    }

    public bool TryGet(string userKey, string projectId, out ProjectInfo? project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_gate)
        {
            ProjectRecord? record = PartitionOf(userKey).Find(p => p.ProjectId == projectId);
            project = record?.ToInfo();
            return record is not null;
        }
    }

    /// <summary>Returns a previously persisted non-General assignment for this session.</summary>
    public string? ProjectOfSession(string userKey, string machineId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            SessionProjectRecord? assignment = SessionProjectsOf(userKey).Find(
                record => record.MachineId == machineId && record.SessionId == sessionId);

            return assignment is not null && PartitionOf(userKey).Exists(
                project => project.ProjectId == assignment.ProjectId)
                ? assignment.ProjectId
                : null;
        }
    }

    /// <summary>
    /// Persists a live session's project. Null means General and removes the durable
    /// override so a reused session id cannot inherit an old choice.
    /// </summary>
    public bool TrySetSessionProject(
        string userKey,
        string machineId,
        string sessionId,
        string? projectId,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_ioGate)
        {
            lock (_gate)
            {
                if (!_persistenceAvailable)
                {
                    error = ErrorCodes.InternalError;
                    return false;
                }

                if (projectId is not null &&
                    !PartitionOf(userKey).Exists(project => project.ProjectId == projectId))
                {
                    error = ErrorCodes.ProjectNotFound;
                    return false;
                }

                List<SessionProjectRecord> assignments = SessionProjectsOf(userKey);
                int index = assignments.FindIndex(
                    record => record.MachineId == machineId && record.SessionId == sessionId);
                SessionProjectRecord? previous = index >= 0 ? assignments[index] : null;

                if (projectId is null)
                {
                    if (index < 0)
                    {
                        error = null;
                        return true;
                    }

                    assignments.RemoveAt(index);
                }
                else if (previous is null)
                {
                    assignments.Add(new SessionProjectRecord
                    {
                        MachineId = machineId,
                        SessionId = sessionId,
                        ProjectId = projectId,
                    });
                }
                else if (previous.ProjectId == projectId)
                {
                    error = null;
                    return true;
                }
                else
                {
                    assignments[index] = new SessionProjectRecord
                    {
                        MachineId = machineId,
                        SessionId = sessionId,
                        ProjectId = projectId,
                    };
                }

                _dirty = true;

                if (!Flush())
                {
                    if (previous is null)
                    {
                        assignments.RemoveAll(
                            record => record.MachineId == machineId && record.SessionId == sessionId);
                    }
                    else if (index >= 0 && index < assignments.Count)
                    {
                        assignments[index] = previous;
                    }
                    else
                    {
                        assignments.Insert(Math.Min(index, assignments.Count), previous);
                    }

                    error = ErrorCodes.InternalError;
                    return false;
                }

                error = null;
                return true;
            }
        }
    }

    public bool TryCreate(
        string userKey,
        string name,
        string? description,
        string? siteUrl,
        string? repoUrl,
        out ProjectInfo? project,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_ioGate)
        {
            lock (_gate)
            {
                if (!_persistenceAvailable)
                {
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                List<ProjectRecord> partition = PartitionOf(userKey);

                if (!TryValidate(name, description, siteUrl, repoUrl, out error))
                {
                    project = null;
                    return false;
                }

                if (NameTaken(partition, name, excludeId: null))
                {
                    project = null;
                    error = ErrorCodes.DuplicateProjectName;
                    return false;
                }

                ProjectRecord record = new()
                {
                    ProjectId = Guid.NewGuid().ToString("n"),
                    Name = name.Trim(),
                    Description = Normalize(description, MaxDescriptionLength),
                    SiteUrl = Normalize(siteUrl, MaxUrlLength),
                    RepoUrl = Normalize(repoUrl, MaxUrlLength),
                    IsGeneral = false,
                    CreatedAt = _time.GetUtcNow(),
                };

                partition.Add(record);
                _dirty = true;

                if (!Flush())
                {
                    partition.Remove(record);
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                project = record.ToInfo();
                error = null;
                return true;
            }
        }
    }

    /// <summary>
    /// Edits a project's fields. General keeps its reserved name, but its optional
    /// metadata remains editable.
    /// </summary>
    public bool TryUpdate(
        string userKey,
        string projectId,
        string name,
        string? description,
        string? siteUrl,
        string? repoUrl,
        out ProjectInfo? project,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_ioGate)
        {
            lock (_gate)
            {
                if (!_persistenceAvailable)
                {
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                List<ProjectRecord> partition = PartitionOf(userKey);
                ProjectRecord? record = partition.Find(p => p.ProjectId == projectId);

                if (record is null)
                {
                    project = null;
                    error = ErrorCodes.ProjectNotFound;
                    return false;
                }

                if (!TryValidate(name, description, siteUrl, repoUrl, out error))
                {
                    project = null;
                    return false;
                }

                if (record.IsGeneral &&
                    !string.Equals(name.Trim(), GeneralProjectName, StringComparison.Ordinal))
                {
                    project = null;
                    error = ErrorCodes.InvalidRequest;
                    return false;
                }

                if (NameTaken(partition, name, excludeId: projectId))
                {
                    project = null;
                    error = ErrorCodes.DuplicateProjectName;
                    return false;
                }

                string oldName = record.Name;
                string? oldDescription = record.Description;
                string? oldSiteUrl = record.SiteUrl;
                string? oldRepoUrl = record.RepoUrl;

                record.Name = name.Trim();
                record.Description = Normalize(description, MaxDescriptionLength);
                record.SiteUrl = Normalize(siteUrl, MaxUrlLength);
                record.RepoUrl = Normalize(repoUrl, MaxUrlLength);
                _dirty = true;

                if (!Flush())
                {
                    record.Name = oldName;
                    record.Description = oldDescription;
                    record.SiteUrl = oldSiteUrl;
                    record.RepoUrl = oldRepoUrl;
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                project = record.ToInfo();
                error = null;
                return true;
            }
        }
    }

    /// <summary>Deletes a project. Refused for General. The caller is responsible for reassigning its sessions.</summary>
    public bool TryDelete(string userKey, string projectId, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        if (string.Equals(projectId, GeneralProjectId, StringComparison.Ordinal))
        {
            error = ErrorCodes.CannotDeleteGeneralProject;
            return false;
        }

        lock (_ioGate)
        {
            lock (_gate)
            {
                if (!_persistenceAvailable)
                {
                    error = ErrorCodes.InternalError;
                    return false;
                }

                List<ProjectRecord> partition = PartitionOf(userKey);
                int index = partition.FindIndex(p => p.ProjectId == projectId);

                if (index < 0)
                {
                    error = ErrorCodes.ProjectNotFound;
                    return false;
                }

                ProjectRecord removed = partition[index];
                List<SessionProjectRecord> assignments = SessionProjectsOf(userKey);
                List<SessionProjectRecord> removedAssignments =
                    assignments.Where(assignment => assignment.ProjectId == projectId).ToList();
                partition.RemoveAt(index);
                assignments.RemoveAll(assignment => assignment.ProjectId == projectId);
                _dirty = true;

                if (!Flush())
                {
                    partition.Insert(index, removed);
                    assignments.AddRange(removedAssignments);
                    error = ErrorCodes.InternalError;
                    return false;
                }
            }
        }

        // The icon file, if any, is best-effort cleanup - a stray file costs nothing
        // and is never served again once the project is gone from the state file.
        try
        {
            lock (_ioGate)
            {
                File.Delete(IconPath(userKey, projectId));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the icon file for deleted project {ProjectId}.", projectId);
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Stores a new icon for a project, bumping <see cref="ProjectInfo.IconVersion"/>
    /// so clients cache-bust. Content type and size are validated here rather than
    /// trusted from the caller, because the caller is an HTTP endpoint fed directly
    /// by a phone.
    /// </summary>
    public bool TrySetIcon(
        string userKey,
        string projectId,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        out ProjectInfo? project,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        if (bytes.Length == 0 || bytes.Length > MaxIconBytes)
        {
            project = null;
            error = ErrorCodes.InvalidRequest;
            return false;
        }

        if (!AllowedIconContentTypes.Contains(contentType))
        {
            project = null;
            error = ErrorCodes.InvalidRequest;
            return false;
        }

        if (!MatchesIconSignature(bytes.Span, contentType))
        {
            project = null;
            error = ErrorCodes.InvalidRequest;
            return false;
        }

        lock (_ioGate)
        {
            lock (_gate)
            {
                if (!_persistenceAvailable)
                {
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                ProjectRecord? record = PartitionOf(userKey).Find(p => p.ProjectId == projectId);

                if (record is null)
                {
                    project = null;
                    error = ErrorCodes.ProjectNotFound;
                    return false;
                }

                string iconPath = IconPath(userKey, projectId);
                byte[]? previousBytes;
                int previousVersion = record.IconVersion;
                string? previousContentType = record.IconContentType;

                try
                {
                    previousBytes = record.IconVersion > 0 && File.Exists(iconPath)
                        ? File.ReadAllBytes(iconPath)
                        : null;
                    WriteAtomically(
                        iconPath,
                        temporary => File.WriteAllBytes(temporary, bytes.ToArray()));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not write the icon file for project {ProjectId}.", projectId);
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                record.IconVersion++;
                record.IconContentType = contentType;
                _dirty = true;

                if (!Flush())
                {
                    record.IconVersion = previousVersion;
                    record.IconContentType = previousContentType;
                    RestoreIcon(iconPath, previousBytes, projectId);
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                project = record.ToInfo();
                error = null;
                return true;
            }
        }
    }

    /// <summary>Resets a project to the client's default icon.</summary>
    public bool TryClearIcon(string userKey, string projectId, out ProjectInfo? project, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_ioGate)
        {
            lock (_gate)
            {
                if (!_persistenceAvailable)
                {
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                ProjectRecord? record = PartitionOf(userKey).Find(p => p.ProjectId == projectId);

                if (record is null)
                {
                    project = null;
                    error = ErrorCodes.ProjectNotFound;
                    return false;
                }

                string iconPath = IconPath(userKey, projectId);
                byte[]? previousBytes;
                int previousVersion = record.IconVersion;
                string? previousContentType = record.IconContentType;

                try
                {
                    previousBytes = record.IconVersion > 0 && File.Exists(iconPath)
                        ? File.ReadAllBytes(iconPath)
                        : null;
                    File.Delete(iconPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not remove the icon file for project {ProjectId}.", projectId);
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                record.IconVersion = 0;
                record.IconContentType = null;
                _dirty = true;

                if (!Flush())
                {
                    record.IconVersion = previousVersion;
                    record.IconContentType = previousContentType;
                    RestoreIcon(iconPath, previousBytes, projectId);
                    project = null;
                    error = ErrorCodes.InternalError;
                    return false;
                }

                project = record.ToInfo();
                error = null;
                return true;
            }
        }
    }

    /// <summary>Reads an uploaded icon's bytes back. False when there is none, scoped to this user's own project.</summary>
    public bool TryReadIcon(string userKey, string projectId, out byte[]? bytes, out string? contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        ProjectRecord? record;

        lock (_gate)
        {
            record = PartitionOf(userKey).Find(p => p.ProjectId == projectId);
        }

        if (record is null || record.IconVersion == 0)
        {
            bytes = null;
            contentType = null;
            return false;
        }

        try
        {
            bytes = File.ReadAllBytes(IconPath(userKey, projectId));
            contentType = record.IconContentType ?? "application/octet-stream";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the icon file for project {ProjectId}.", projectId);
            bytes = null;
            contentType = null;
            return false;
        }
    }

    /// <summary>Writes the file if anything changed. Cheap and safe to call after every mutation.</summary>
    private bool Flush()
    {
        lock (_ioGate)
        {
            string json;

            lock (_gate)
            {
                if (!_dirty || !_persistenceAvailable)
                {
                    return _persistenceAvailable;
                }

                json = JsonSerializer.Serialize(_state, Json);
                _dirty = false;
            }

            try
            {
                WriteAtomically(StatePath, temporary => File.WriteAllText(temporary, json));
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    _dirty = true;
                    _persistenceAvailable = false;
                }

                _logger.LogWarning(error, "Could not write the project state file; project mutations are disabled.");
                return false;
            }
        }
    }

    /// <summary>Caller must hold the gate. Creates the partition and seeds General if either is missing.</summary>
    private List<ProjectRecord> PartitionOf(string userKey)
    {
        if (!_state.Projects.TryGetValue(userKey, out List<ProjectRecord>? partition))
        {
            partition = [];
            _state.Projects[userKey] = partition;
        }

        if (!partition.Exists(p => p.ProjectId == GeneralProjectId))
        {
            partition.Insert(0, new ProjectRecord
            {
                ProjectId = GeneralProjectId,
                Name = GeneralProjectName,
                IsGeneral = true,
                CreatedAt = _time.GetUtcNow(),
            });

            _dirty = true;
        }

        return partition;
    }

    /// <summary>Caller must hold the gate.</summary>
    private List<SessionProjectRecord> SessionProjectsOf(string userKey)
    {
        if (!_state.SessionProjects.TryGetValue(userKey, out List<SessionProjectRecord>? assignments))
        {
            assignments = [];
            _state.SessionProjects[userKey] = assignments;
        }

        return assignments;
    }

    /// <summary>
    /// Case-insensitive per-user uniqueness, General included - so nobody can create
    /// a second project that collides with the reserved one by name either.
    /// </summary>
    private static bool NameTaken(List<ProjectRecord> partition, string name, string? excludeId)
    {
        string trimmed = name.Trim();
        return partition.Exists(p =>
            p.ProjectId != excludeId && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryValidate(
        string name,
        string? description,
        string? siteUrl,
        string? repoUrl,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            error = ErrorCodes.InvalidRequest;
            return false;
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            error = ErrorCodes.InvalidRequest;
            return false;
        }

        if (!IsValidOptionalUrl(siteUrl))
        {
            error = ErrorCodes.InvalidProjectSiteUrl;
            return false;
        }

        if (!IsValidOptionalUrl(repoUrl))
        {
            error = ErrorCodes.InvalidProjectRepoUrl;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Blank is fine - both URLs are optional. Anything present must be an absolute http(s) URL.</summary>
    private static bool IsValidOptionalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        return url.Length <= MaxUrlLength
            && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>A user key contains colons, which are not safe path segments on every filesystem.</summary>
    private static string SanitizeForPath(string userKey) => userKey.Replace(':', '_');

    private string IconDirectory(string userKey) => Path.Combine(IconRoot, SanitizeForPath(userKey));

    private string IconPath(string userKey, string projectId) => Path.Combine(IconDirectory(userKey), projectId);

    private static bool MatchesIconSignature(ReadOnlySpan<byte> bytes, string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "image/png" =>
                bytes.Length >= 8 &&
                bytes[..8].SequenceEqual(PngSignature),
            "image/jpeg" =>
                bytes.Length >= 3 &&
                bytes[0] == 0xff &&
                bytes[1] == 0xd8 &&
                bytes[2] == 0xff,
            "image/webp" =>
                bytes.Length >= 12 &&
                bytes[..4].SequenceEqual("RIFF"u8) &&
                bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };

    private static void WriteAtomically(string path, Action<string> write)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            write(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of an unpublished temporary file.
            }
        }
    }

    private void RestoreIcon(string path, byte[]? previousBytes, string projectId)
    {
        try
        {
            if (previousBytes is null)
            {
                File.Delete(path);
            }
            else
            {
                WriteAtomically(path, temporary => File.WriteAllBytes(temporary, previousBytes));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(error, "Could not restore the icon file after project {ProjectId} failed to persist.", projectId);
        }
    }

    private ProjectState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new ProjectState();
            }

            ProjectState state =
                JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(StatePath), Json) ?? new ProjectState();
            state.Schema = 2;
            state.Projects ??= new Dictionary<string, List<ProjectRecord>>(StringComparer.Ordinal);
            state.SessionProjects ??=
                new Dictionary<string, List<SessionProjectRecord>>(StringComparer.Ordinal);
            return state;
        }
        catch (JsonException error)
        {
            string backup = $"{StatePath}.corrupt-{_time.GetUtcNow():yyyyMMddHHmmssfff}";

            try
            {
                File.Copy(StatePath, backup, overwrite: false);
                _logger.LogError(
                    error,
                    "The project state file was corrupt. It was preserved at {BackupPath}; starting from empty.",
                    backup);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            {
                _persistenceAvailable = false;
                _logger.LogError(
                    backupError,
                    "The corrupt project state file could not be backed up. Project mutations are disabled.");
            }

            return new ProjectState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _persistenceAvailable = false;
            _logger.LogError(error, "The project state file could not be read. Project mutations are disabled.");
            return new ProjectState();
        }
    }

    /// <summary>Same convention as <c>OperatorStateStore.ResolvePath</c> - see that type for the rationale.</summary>
    internal static string ResolvePath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string? home = Environment.GetEnvironmentVariable("HOME");

        string root = !string.IsNullOrWhiteSpace(home) && Directory.Exists(home)
            ? Path.Combine(home, "data")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(root, "1RemoteCLI", "project-state.json");
    }

    internal static string ResolveIconRoot(string configured, string statePath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string directory = Path.GetDirectoryName(statePath) ?? string.Empty;
        return Path.Combine(directory, "project-icons");
    }
}
