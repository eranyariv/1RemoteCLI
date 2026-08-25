using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>
/// Stages browser-selected chat attachments on disk until the prompt that carries
/// them is submitted.
/// <para>
/// Disk rather than memory because the bytes arrive one chunk at a time over a
/// connection that may be slow, and a phone that puts four files in a composer and
/// then walks into a lift would otherwise pin all of them in the agent's heap for as
/// long as it takes them to give up. Nothing here is ever named to the user: the path
/// exists so the bytes have somewhere to wait, and a chat attachment that leaked a
/// machine path would be a terminal upload with extra steps.
/// </para>
/// <para>
/// Every attachment is owned by exactly one session and one client connection.
/// Sending a prompt consumes it, cancelling removes it, and detaching, disconnecting,
/// or the session disappearing removes everything that client staged.
/// </para>
/// </summary>
internal sealed class ChatAttachmentStore : IDisposable
{
    internal static readonly TimeSpan StaleRetention = TimeSpan.FromHours(6);
    internal static readonly TimeSpan StaleSweepInterval = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, StagedAttachment> _attachments = [];
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ITimer _staleTimer;

    public ChatAttachmentStore(string? root = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Path.GetTempPath(),
            "1RemoteCLI",
            "chat-attachments"));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        DeleteStaleDirectories();
        _staleTimer = _time.CreateTimer(
            static state => ((ChatAttachmentStore)state!).DeleteStaleDirectories(),
            this,
            StaleSweepInterval,
            StaleSweepInterval);
    }

    public ChatAttachmentReply Begin(BeginChatAttachmentNotification request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Guid.TryParse(request.AttachmentId, out Guid attachmentId) ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            string.IsNullOrWhiteSpace(request.ClientConnectionId) ||
            string.IsNullOrWhiteSpace(request.FileName) ||
            request.FileName.Length > ChatAttachmentLimits.MaxFileNameChars ||
            (request.MimeType?.Length ?? 0) > ChatAttachmentLimits.MaxMimeTypeChars ||
            request.TotalBytes <= 0)
        {
            return Failure(
                request.AttachmentId,
                request.TotalBytes,
                ErrorCodes.InvalidRequest,
                "That attachment's details are not usable.");
        }

        if (request.TotalBytes > ChatAttachmentLimits.MaxAttachmentBytes)
        {
            return Failure(
                request.AttachmentId,
                request.TotalBytes,
                ErrorCodes.AttachmentTooLarge,
                $"Chat attachments are limited to {ChatAttachmentLimits.MaxAttachmentBytes / (1024 * 1024)} MB each.");
        }

        string safeName = SafeTempFiles.SanitizeFileName(request.FileName);
        string sessionDirectory = SafeTempFiles.ContainedPath(_root, request.SessionId);
        string directory = SafeTempFiles.ContainedPath(sessionDirectory, attachmentId.ToString("n"));
        string path = SafeTempFiles.ContainedPath(directory, safeName);

        lock (_gate)
        {
            if (_attachments.ContainsKey(attachmentId) || Directory.Exists(directory))
            {
                return Failure(
                    request.AttachmentId,
                    request.TotalBytes,
                    ErrorCodes.InvalidRequest,
                    "That attachment id is already in use.");
            }

            StagedAttachment[] owned = Owned(request.SessionId, request.ClientConnectionId);
            if (owned.Length >= ChatAttachmentLimits.MaxAttachmentCount)
            {
                return Failure(
                    request.AttachmentId,
                    request.TotalBytes,
                    ErrorCodes.AttachmentBudgetExceeded,
                    $"A prompt carries at most {ChatAttachmentLimits.MaxAttachmentCount} attachments.");
            }

            if (owned.Sum(item => item.TotalBytes) + request.TotalBytes > ChatAttachmentLimits.MaxPromptBytes)
            {
                return Failure(
                    request.AttachmentId,
                    request.TotalBytes,
                    ErrorCodes.AttachmentBudgetExceeded,
                    $"All attachments on one prompt are limited to {ChatAttachmentLimits.MaxPromptBytes / (1024 * 1024)} MB.");
            }

            try
            {
                Directory.CreateDirectory(directory);
                var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    ChatAttachmentLimits.MaxChunkBytes,
                    FileOptions.SequentialScan);

                _attachments.Add(
                    attachmentId,
                    new StagedAttachment(
                        request.SessionId,
                        request.ClientConnectionId,
                        request.FileName,
                        ChatAttachmentPolicy.Normalize(request.MimeType),
                        request.TotalBytes,
                        directory,
                        path,
                        stream));

                return Progress(request.AttachmentId, 0, request.TotalBytes, completed: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFileFailure(ex);
                DeleteDirectory(directory);
                return Failure(
                    request.AttachmentId,
                    request.TotalBytes,
                    ErrorCodes.AttachmentFailed,
                    "The machine could not stage that attachment.");
            }
        }
    }

    public ChatAttachmentReply Append(ChatAttachmentChunkNotification request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Guid.TryParse(request.AttachmentId, out Guid attachmentId) ||
            request.Offset < 0 ||
            request.Data is null ||
            request.Data.Length == 0 ||
            request.Data.Length > ChatAttachmentLimits.MaxChunkBytes)
        {
            return Failure(request.AttachmentId, 0, ErrorCodes.InvalidRequest, "That attachment chunk is not usable.");
        }

        lock (_gate)
        {
            if (!TryOwned(attachmentId, request.SessionId, request.ClientConnectionId, out StagedAttachment? staged) ||
                staged.Completed)
            {
                return Failure(
                    request.AttachmentId,
                    0,
                    ErrorCodes.AttachmentNotFound,
                    "That attachment is no longer being uploaded.");
            }

            if (request.Offset != staged.ConfirmedBytes ||
                request.Data.LongLength > staged.TotalBytes - staged.ConfirmedBytes)
            {
                return Failure(
                    request.AttachmentId,
                    staged.TotalBytes,
                    ErrorCodes.InvalidRequest,
                    "Attachment chunks must arrive once and in order.",
                    staged.ConfirmedBytes);
            }

            try
            {
                staged.Stream!.Write(request.Data);
                staged.ConfirmedBytes += request.Data.LongLength;

                if (staged.ConfirmedBytes != staged.TotalBytes)
                {
                    return Progress(
                        request.AttachmentId,
                        staged.ConfirmedBytes,
                        staged.TotalBytes,
                        completed: false);
                }

                staged.Stream.Flush(flushToDisk: true);
                staged.Stream.Dispose();
                staged.Stream = null;
                staged.Completed = true;

                return Progress(
                    request.AttachmentId,
                    staged.ConfirmedBytes,
                    staged.TotalBytes,
                    completed: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFileFailure(ex);
                long confirmed = staged.ConfirmedBytes;
                Remove(attachmentId, staged);
                return Failure(
                    request.AttachmentId,
                    staged.TotalBytes,
                    ErrorCodes.AttachmentFailed,
                    "The machine could not stage that attachment.",
                    confirmed);
            }
        }
    }

    /// <summary>
    /// Removes one attachment, staged or complete. Reported as a cancellation rather
    /// than a success because the browser is removing something it had already been
    /// told about, and "gone" is the outcome it needs either way.
    /// </summary>
    public ChatAttachmentReply Cancel(CancelChatAttachmentNotification request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Guid.TryParse(request.AttachmentId, out Guid attachmentId))
        {
            return Failure(request.AttachmentId, 0, ErrorCodes.InvalidRequest, "That attachment id is not usable.");
        }

        lock (_gate)
        {
            if (!TryOwned(attachmentId, request.SessionId, request.ClientConnectionId, out StagedAttachment? staged))
            {
                return Failure(
                    request.AttachmentId,
                    0,
                    ErrorCodes.AttachmentNotFound,
                    "That attachment is no longer staged.");
            }

            long confirmed = staged.ConfirmedBytes;
            long total = staged.TotalBytes;
            Remove(attachmentId, staged);

            return Failure(
                request.AttachmentId,
                total,
                ErrorCodes.AttachmentCancelled,
                "The attachment was removed.",
                confirmed);
        }
    }

    /// <summary>
    /// Reads the completed attachments a prompt names, in the order it named them.
    /// <para>
    /// Reads but does not delete: the bytes are only discarded once the ACP agent has
    /// accepted the prompt, so a prompt rejected for any reason leaves the user's
    /// selection intact and correctable.
    /// </para>
    /// </summary>
    public bool TryRead(
        string sessionId,
        string clientConnectionId,
        IReadOnlyList<string> attachmentIds,
        out IReadOnlyList<ChatAttachmentContent> contents,
        out ChatPromptReply? error)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);

        contents = [];
        error = null;

        if (attachmentIds.Count > ChatAttachmentLimits.MaxAttachmentCount)
        {
            error = PromptFailure(
                ErrorCodes.AttachmentBudgetExceeded,
                $"A prompt carries at most {ChatAttachmentLimits.MaxAttachmentCount} attachments.");
            return false;
        }

        var read = new List<ChatAttachmentContent>(attachmentIds.Count);
        long total = 0;

        lock (_gate)
        {
            foreach (string id in attachmentIds)
            {
                if (!Guid.TryParse(id, out Guid attachmentId) ||
                    !TryOwned(attachmentId, sessionId, clientConnectionId, out StagedAttachment? staged) ||
                    !staged.Completed)
                {
                    error = PromptFailure(
                        ErrorCodes.AttachmentNotFound,
                        "One of those attachments is no longer ready to send.");
                    return false;
                }

                total += staged.TotalBytes;
                if (total > ChatAttachmentLimits.MaxPromptBytes)
                {
                    error = PromptFailure(
                        ErrorCodes.AttachmentBudgetExceeded,
                        $"All attachments on one prompt are limited to {ChatAttachmentLimits.MaxPromptBytes / (1024 * 1024)} MB.");
                    return false;
                }

                try
                {
                    byte[] bytes = File.ReadAllBytes(staged.Path);
                    if (bytes.LongLength != staged.TotalBytes)
                    {
                        error = PromptFailure(
                            ErrorCodes.AttachmentFailed,
                            "One of those attachments changed on disk before it could be sent.");
                        return false;
                    }

                    read.Add(
                        new ChatAttachmentContent(
                            id,
                            staged.FileName,
                            staged.DeclaredMediaType,
                            bytes));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogFileFailure(ex);
                    error = PromptFailure(
                        ErrorCodes.AttachmentFailed,
                        "The machine could not read one of those attachments.");
                    return false;
                }
            }
        }

        contents = read;
        return true;
    }

    /// <summary>Deletes attachments a prompt successfully consumed.</summary>
    public void Consume(string sessionId, string clientConnectionId, IReadOnlyList<string> attachmentIds)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);

        lock (_gate)
        {
            foreach (string id in attachmentIds)
            {
                if (Guid.TryParse(id, out Guid attachmentId) &&
                    TryOwned(attachmentId, sessionId, clientConnectionId, out StagedAttachment? staged))
                {
                    Remove(attachmentId, staged);
                }
            }
        }
    }

    public void RemoveForClient(string sessionId, string clientConnectionId)
    {
        lock (_gate)
        {
            foreach ((Guid id, StagedAttachment staged) in _attachments
                         .Where(pair =>
                             pair.Value.SessionId == sessionId &&
                             pair.Value.ClientConnectionId == clientConnectionId)
                         .ToArray())
            {
                Remove(id, staged);
            }
        }
    }

    public void RemoveSession(string sessionId)
    {
        lock (_gate)
        {
            foreach ((Guid id, StagedAttachment staged) in _attachments
                         .Where(pair => pair.Value.SessionId == sessionId)
                         .ToArray())
            {
                Remove(id, staged);
            }

            try
            {
                DeleteDirectory(SafeTempFiles.ContainedPath(_root, sessionId));
            }
            catch (InvalidOperationException)
            {
                // A session id that cannot form a contained path never produced one.
            }
        }
    }

    /// <summary>Drops everything, which is what a lost relay connection means for staging.</summary>
    public void RemoveAll()
    {
        lock (_gate)
        {
            foreach ((Guid id, StagedAttachment staged) in _attachments.ToArray())
            {
                Remove(id, staged);
            }
        }
    }

    private StagedAttachment[] Owned(string sessionId, string clientConnectionId) =>
    [
        .. _attachments.Values.Where(item =>
            item.SessionId == sessionId &&
            item.ClientConnectionId == clientConnectionId),
    ];

    private bool TryOwned(
        Guid attachmentId,
        string sessionId,
        string clientConnectionId,
        out StagedAttachment staged)
    {
        if (_attachments.TryGetValue(attachmentId, out StagedAttachment? found) &&
            found.SessionId == sessionId &&
            found.ClientConnectionId == clientConnectionId)
        {
            staged = found;
            return true;
        }

        staged = null!;
        return false;
    }

    private void Remove(Guid attachmentId, StagedAttachment staged)
    {
        _attachments.Remove(attachmentId);
        staged.Stream?.Dispose();
        staged.Stream = null;
        DeleteDirectory(staged.Directory);
    }

    private void DeleteStaleDirectories()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            DateTimeOffset cutoff = _time.GetUtcNow() - StaleRetention;

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(_root))
                {
                    if (Directory.GetLastWriteTimeUtc(directory) >= cutoff.UtcDateTime ||
                        _attachments.Values.Any(item => SafeTempFiles.ContainedBy(directory, item.Directory)))
                    {
                        continue;
                    }

                    DeleteDirectory(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogCleanupFailure(ex);
            }
        }
    }

    private void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailure(ex);
        }
    }

    /// <summary>Never the name, never the type, and above all never the bytes.</summary>
    private void LogFileFailure(Exception error) =>
        _logger.LogWarning(
            "A chat attachment file operation failed ({ErrorType}).",
            error.GetType().Name);

    private void LogCleanupFailure(Exception error) =>
        _logger.LogWarning(
            "A chat attachment directory could not be removed ({ErrorType}).",
            error.GetType().Name);

    private static ChatAttachmentReply Progress(
        string attachmentId,
        long confirmed,
        long total,
        bool completed) =>
        new()
        {
            AttachmentId = attachmentId,
            ConfirmedBytes = confirmed,
            TotalBytes = total,
            Completed = completed,
        };

    private static ChatAttachmentReply Failure(
        string attachmentId,
        long total,
        string code,
        string message,
        long confirmed = 0) =>
        new()
        {
            AttachmentId = attachmentId,
            ConfirmedBytes = confirmed,
            TotalBytes = total,
            ErrorCode = code,
            ErrorMessage = message,
        };

    private static ChatPromptReply PromptFailure(string code, string message) =>
        new()
        {
            Accepted = false,
            ErrorCode = code,
            ErrorMessage = message,
        };

    private sealed class StagedAttachment(
        string sessionId,
        string clientConnectionId,
        string fileName,
        string declaredMediaType,
        long totalBytes,
        string directory,
        string path,
        FileStream stream)
    {
        public string SessionId { get; } = sessionId;
        public string ClientConnectionId { get; } = clientConnectionId;
        public string FileName { get; } = fileName;
        public string DeclaredMediaType { get; } = declaredMediaType;
        public long TotalBytes { get; } = totalBytes;
        public string Directory { get; } = directory;
        public string Path { get; } = path;
        public FileStream? Stream { get; set; } = stream;
        public long ConfirmedBytes { get; set; }
        public bool Completed { get; set; }
    }

    public void Dispose()
    {
        _staleTimer.Dispose();
        RemoveAll();
    }
}

/// <summary>One staged attachment, read back for the prompt that is about to carry it.</summary>
public sealed record ChatAttachmentContent(
    string AttachmentId,
    string FileName,
    string DeclaredMediaType,
    byte[] Bytes);
