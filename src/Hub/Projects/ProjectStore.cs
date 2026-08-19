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

/// <summary>The whole store, on disk, in one JSON file. See <see cref="ProjectStore"/> for why.</summary>
public sealed class ProjectState
{
    /// <summary>Schema version, so a future shape change can be migrated rather than guessed at.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>User key to that user's projects, General included.</summary>
    public Dictionary<string, List<ProjectRecord>> Projects { get; set; } = new(StringComparer.Ordinal);
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
/// <b>The General project is lazily seeded, never deleted, and freely edited.</b>
/// Every partition access ensures it exists first, so a user who has never touched
/// projects still has exactly one - the one new sessions default to. The issue
/// requires only that it cannot be deleted, not that it cannot be renamed, so update
/// works on it like any other project.
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

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<ProjectStore> _logger;
    private readonly ProjectState _state;

    private bool _dirty;

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

        ProjectRecord record;

        lock (_gate)
        {
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

            record = new ProjectRecord
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
        }

        Flush();

        project = record.ToInfo();
        error = null;
        return true;
    }

    /// <summary>
    /// Edits a project's fields. Works on General too - only deletion is refused for
    /// it, per the issue's requirement and this type's own remarks.
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

        ProjectRecord? record;

        lock (_gate)
        {
            List<ProjectRecord> partition = PartitionOf(userKey);
            record = partition.Find(p => p.ProjectId == projectId);

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

            if (NameTaken(partition, name, excludeId: projectId))
            {
                project = null;
                error = ErrorCodes.DuplicateProjectName;
                return false;
            }

            record.Name = name.Trim();
            record.Description = Normalize(description, MaxDescriptionLength);
            record.SiteUrl = Normalize(siteUrl, MaxUrlLength);
            record.RepoUrl = Normalize(repoUrl, MaxUrlLength);
            _dirty = true;
        }

        Flush();

        project = record.ToInfo();
        error = null;
        return true;
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

        lock (_gate)
        {
            List<ProjectRecord> partition = PartitionOf(userKey);
            int index = partition.FindIndex(p => p.ProjectId == projectId);

            if (index < 0)
            {
                error = ErrorCodes.ProjectNotFound;
                return false;
            }

            partition.RemoveAt(index);
            _dirty = true;
        }

        Flush();

        // The icon file, if any, is best-effort cleanup - a stray file costs nothing
        // and is never served again once the project is gone from the state file.
        try
        {
            File.Delete(IconPath(userKey, projectId));
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

        ProjectRecord? record;

        lock (_gate)
        {
            record = PartitionOf(userKey).Find(p => p.ProjectId == projectId);

            if (record is null)
            {
                project = null;
                error = ErrorCodes.ProjectNotFound;
                return false;
            }

            record.IconVersion++;
            record.IconContentType = contentType;
            _dirty = true;
        }

        try
        {
            string directory = IconDirectory(userKey);
            Directory.CreateDirectory(directory);

            string path = IconPath(userKey, projectId);
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the icon file for project {ProjectId}.", projectId);
            project = null;
            error = ErrorCodes.InternalError;
            return false;
        }

        Flush();

        project = record.ToInfo();
        error = null;
        return true;
    }

    /// <summary>Resets a project to the client's default icon.</summary>
    public bool TryClearIcon(string userKey, string projectId, out ProjectInfo? project, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        ProjectRecord? record;

        lock (_gate)
        {
            record = PartitionOf(userKey).Find(p => p.ProjectId == projectId);

            if (record is null)
            {
                project = null;
                error = ErrorCodes.ProjectNotFound;
                return false;
            }

            record.IconVersion = 0;
            record.IconContentType = null;
            _dirty = true;
        }

        try
        {
            File.Delete(IconPath(userKey, projectId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the icon file for project {ProjectId}.", projectId);
        }

        Flush();

        project = record.ToInfo();
        error = null;
        return true;
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
    public void Flush()
    {
        string json;

        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            json = JsonSerializer.Serialize(_state, Json);
            _dirty = false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(StatePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing a project create or edit is a materially bad experience, but taking
            // the hub down over a filesystem hiccup would be worse. The state stays in
            // memory - correct for the rest of this process's life - and the next
            // mutation's flush will try writing it again.
            lock (_gate)
            {
                _dirty = true;
            }

            _logger.LogWarning(error, "Could not write the project state file.");
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

        if (!IsValidOptionalUrl(siteUrl) || !IsValidOptionalUrl(repoUrl))
        {
            error = ErrorCodes.InvalidRequest;
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

    private ProjectState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new ProjectState();
            }

            return JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(StatePath), Json) ?? new ProjectState();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Starting fresh beats refusing to start. The worst case is every user's
            // General project being re-seeded and any custom projects lost, which is
            // recoverable by hand from a text editor if the file is merely corrupt.
            _logger.LogWarning(error, "The project state file could not be read; starting from empty.");
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
