using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// Writes browser-selected files into session-scoped temporary directories.
/// Completed files live until their terminal session ends; partial files also disappear
/// when their client detaches or the relay connection drops.
/// </summary>
internal sealed class TerminalUploadStore : IDisposable
{
    internal static readonly TimeSpan StaleRetention = TimeSpan.FromHours(24);
    internal static readonly TimeSpan StaleSweepInterval = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveUpload> _active = [];
    private readonly Dictionary<Guid, CompletedUpload> _completed = [];
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ITimer _staleTimer;

    public TerminalUploadStore(string? root = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Path.GetTempPath(),
            "1RemoteCLI",
            "terminal-uploads"));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        DeleteStaleDirectories();
        _staleTimer = _time.CreateTimer(
            static state => ((TerminalUploadStore)state!).DeleteStaleDirectories(),
            this,
            StaleSweepInterval,
            StaleSweepInterval);
    }

    public TerminalUploadReply Begin(
        BeginTerminalUploadNotification request,
        TerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);

        if (!TryUploadId(request.UploadId, out Guid uploadId) ||
            string.IsNullOrWhiteSpace(request.ClientConnectionId) ||
            string.IsNullOrWhiteSpace(request.FileName) ||
            request.FileName.Length > TerminalUploadLimits.MaxFileNameChars ||
            request.TotalBytes < 0)
        {
            return Failure(request.UploadId, request.TotalBytes, ErrorCodes.InvalidRequest, "Invalid upload metadata.");
        }

        if (request.TotalBytes > TerminalUploadLimits.MaxFileBytes)
        {
            return Failure(
                request.UploadId,
                request.TotalBytes,
                ErrorCodes.FileTooLarge,
                $"Files are limited to {TerminalUploadLimits.MaxFileBytes / (1024 * 1024)} MB.");
        }

        string safeName = SanitizeFileName(request.FileName);
        string sessionDirectory = ContainedPath(_root, session.SessionId);
        string uploadDirectory = ContainedPath(sessionDirectory, uploadId.ToString("n"));
        string finalPath = ContainedPath(uploadDirectory, safeName);
        string partialPath = ContainedPath(uploadDirectory, ".partial");

        lock (_gate)
        {
            if (_active.ContainsKey(uploadId) || Directory.Exists(uploadDirectory))
            {
                return Failure(
                    request.UploadId,
                    request.TotalBytes,
                    ErrorCodes.InvalidRequest,
                    "That upload id is already in use.");
            }

            try
            {
                Directory.CreateDirectory(uploadDirectory);

                if (request.TotalBytes == 0)
                {
                    using (new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                    }

                    _completed.Add(
                        uploadId,
                        new CompletedUpload(
                            request.SessionId,
                            request.ClientConnectionId,
                            request.TotalBytes,
                            uploadDirectory));
                    return Success(request.UploadId, 0, 0, finalPath);
                }

                var stream = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    TerminalUploadLimits.MaxChunkBytes,
                    FileOptions.SequentialScan);

                _active.Add(
                    uploadId,
                    new ActiveUpload(
                        request.UploadId,
                        request.SessionId,
                        request.ClientConnectionId,
                        request.TotalBytes,
                        uploadDirectory,
                        partialPath,
                        finalPath,
                        stream));

                return Success(request.UploadId, 0, request.TotalBytes);
            }
            catch (IOException ex)
            {
                LogFileFailure(ex);
                DeleteDirectory(uploadDirectory);
                return Failure(request.UploadId, request.TotalBytes, ErrorCodes.UploadFailed, "The file could not be created.");
            }
            catch (UnauthorizedAccessException ex)
            {
                LogFileFailure(ex);
                DeleteDirectory(uploadDirectory);
                return Failure(request.UploadId, request.TotalBytes, ErrorCodes.UploadFailed, "The file could not be created.");
            }
        }
    }

    public TerminalUploadReply Append(TerminalUploadChunkNotification request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryUploadId(request.UploadId, out Guid uploadId) ||
            request.Offset < 0 ||
            request.Data is null ||
            request.Data.Length == 0 ||
            request.Data.Length > TerminalUploadLimits.MaxChunkBytes)
        {
            return Failure(request.UploadId, 0, ErrorCodes.InvalidRequest, "Invalid upload chunk.");
        }

        lock (_gate)
        {
            if (!_active.TryGetValue(uploadId, out ActiveUpload? upload) ||
                upload.SessionId != request.SessionId ||
                upload.ClientConnectionId != request.ClientConnectionId)
            {
                return Failure(request.UploadId, 0, ErrorCodes.UploadNotFound, "That upload is no longer active.");
            }

            if (request.Offset != upload.ConfirmedBytes ||
                request.Data.LongLength > upload.TotalBytes - upload.ConfirmedBytes)
            {
                return Failure(
                    request.UploadId,
                    upload.TotalBytes,
                    ErrorCodes.InvalidRequest,
                    "Upload chunks must arrive once and in order.",
                    upload.ConfirmedBytes);
            }

            try
            {
                upload.Stream.Write(request.Data);
                upload.ConfirmedBytes += request.Data.LongLength;

                if (upload.ConfirmedBytes != upload.TotalBytes)
                {
                    return Success(request.UploadId, upload.ConfirmedBytes, upload.TotalBytes);
                }

                upload.Stream.Flush(flushToDisk: true);
                upload.Stream.Dispose();
                File.Move(upload.PartialPath, upload.FinalPath, overwrite: false);
                _active.Remove(uploadId);
                _completed.Add(
                    uploadId,
                    new CompletedUpload(
                        upload.SessionId,
                        upload.ClientConnectionId,
                        upload.TotalBytes,
                        upload.UploadDirectory));

                return Success(request.UploadId, upload.ConfirmedBytes, upload.TotalBytes, upload.FinalPath);
            }
            catch (IOException ex)
            {
                LogFileFailure(ex);
                Remove(uploadId, upload);
                return Failure(
                    request.UploadId,
                    upload.TotalBytes,
                    ErrorCodes.UploadFailed,
                    "The file could not be saved.",
                    upload.ConfirmedBytes);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogFileFailure(ex);
                Remove(uploadId, upload);
                return Failure(
                    request.UploadId,
                    upload.TotalBytes,
                    ErrorCodes.UploadFailed,
                    "The file could not be saved.",
                    upload.ConfirmedBytes);
            }
        }
    }

    public TerminalUploadReply Cancel(CancelTerminalUploadNotification request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryUploadId(request.UploadId, out Guid uploadId))
        {
            return Failure(request.UploadId, 0, ErrorCodes.InvalidRequest, "Invalid upload id.");
        }

        lock (_gate)
        {
            if (_active.TryGetValue(uploadId, out ActiveUpload? upload) &&
                upload.SessionId == request.SessionId &&
                upload.ClientConnectionId == request.ClientConnectionId)
            {
                long confirmed = upload.ConfirmedBytes;
                long total = upload.TotalBytes;
                Remove(uploadId, upload);

                return Failure(request.UploadId, total, ErrorCodes.UploadCancelled, "The upload was cancelled.", confirmed);
            }

            // The final write can complete while the browser's cancel tap is in flight.
            // Retain just enough ownership metadata to remove that newly completed file
            // instead of reporting cancellation while leaving it behind.
            if (_completed.TryGetValue(uploadId, out CompletedUpload? completed) &&
                completed.SessionId == request.SessionId &&
                completed.ClientConnectionId == request.ClientConnectionId)
            {
                _completed.Remove(uploadId);
                DeleteDirectory(completed.UploadDirectory);
                return Failure(
                    request.UploadId,
                    completed.TotalBytes,
                    ErrorCodes.UploadCancelled,
                    "The upload was cancelled.",
                    completed.TotalBytes);
            }

            return Failure(request.UploadId, 0, ErrorCodes.UploadNotFound, "That upload is no longer active.");
        }
    }

    public void CancelForClient(string sessionId, string clientConnectionId)
    {
        lock (_gate)
        {
            foreach ((Guid uploadId, ActiveUpload upload) in _active
                         .Where(pair =>
                             pair.Value.SessionId == sessionId &&
                             pair.Value.ClientConnectionId == clientConnectionId)
                         .ToArray())
            {
                Remove(uploadId, upload);
            }
        }
    }

    public void CancelActive()
    {
        lock (_gate)
        {
            foreach ((Guid uploadId, ActiveUpload upload) in _active.ToArray())
            {
                Remove(uploadId, upload);
            }
        }
    }

    public void RemoveSession(string sessionId)
    {
        string sessionDirectory = ContainedPath(_root, sessionId);

        lock (_gate)
        {
            foreach ((Guid uploadId, ActiveUpload upload) in _active
                         .Where(pair => pair.Value.SessionId == sessionId)
                         .ToArray())
            {
                Remove(uploadId, upload);
            }

            foreach (Guid uploadId in _completed
                         .Where(pair => pair.Value.SessionId == sessionId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _completed.Remove(uploadId);
            }

            DeleteDirectory(sessionDirectory);
        }
    }

    private static TerminalUploadReply Success(
        string uploadId,
        long confirmed,
        long total,
        string? remotePath = null) =>
        new()
        {
            UploadId = uploadId,
            ConfirmedBytes = confirmed,
            TotalBytes = total,
            RemotePath = remotePath,
        };

    private static TerminalUploadReply Failure(
        string uploadId,
        long total,
        string code,
        string message,
        long confirmed = 0) =>
        new()
        {
            UploadId = uploadId,
            ConfirmedBytes = confirmed,
            TotalBytes = total,
            ErrorCode = code,
            ErrorMessage = message,
        };

    private void Remove(Guid uploadId, ActiveUpload upload)
    {
        _active.Remove(uploadId);
        upload.Stream.Dispose();
        DeleteDirectory(upload.UploadDirectory);
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
                    if (Directory.GetLastWriteTimeUtc(directory) >= cutoff.UtcDateTime)
                    {
                        continue;
                    }

                    foreach (Guid uploadId in _completed
                                 .Where(pair => ContainedBy(directory, pair.Value.UploadDirectory))
                                 .Select(pair => pair.Key)
                                 .ToArray())
                    {
                        _completed.Remove(uploadId);
                    }

                    DeleteDirectory(directory);
                }
            }
            catch (IOException ex)
            {
                LogCleanupFailure(ex);
            }
            catch (UnauthorizedAccessException ex)
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
        catch (IOException ex)
        {
            LogCleanupFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogCleanupFailure(ex);
        }
    }

    private void LogFileFailure(Exception error) =>
        _logger.LogWarning(
            "A terminal upload file operation failed ({ErrorType}).",
            error.GetType().Name);

    private void LogCleanupFailure(Exception error) =>
        _logger.LogWarning(
            "A terminal upload directory could not be removed ({ErrorType}).",
            error.GetType().Name);

    private static bool TryUploadId(string value, out Guid uploadId) =>
        Guid.TryParse(value, out uploadId);

    private static bool ContainedBy(string root, string path) =>
        SafeTempFiles.ContainedBy(root, path);

    private static string SanitizeFileName(string original) =>
        SafeTempFiles.SanitizeFileName(original);

    private static string ContainedPath(string root, string child) =>
        SafeTempFiles.ContainedPath(root, child);

    private sealed class ActiveUpload(
        string uploadId,
        string sessionId,
        string clientConnectionId,
        long totalBytes,
        string uploadDirectory,
        string partialPath,
        string finalPath,
        FileStream stream)
    {
        public string UploadId { get; } = uploadId;
        public string SessionId { get; } = sessionId;
        public string ClientConnectionId { get; } = clientConnectionId;
        public long TotalBytes { get; } = totalBytes;
        public string UploadDirectory { get; } = uploadDirectory;
        public string PartialPath { get; } = partialPath;
        public string FinalPath { get; } = finalPath;
        public FileStream Stream { get; } = stream;
        public long ConfirmedBytes { get; set; }
    }

    private sealed record CompletedUpload(
        string SessionId,
        string ClientConnectionId,
        long TotalBytes,
        string UploadDirectory);

    public void Dispose()
    {
        _staleTimer.Dispose();
        CancelActive();
    }
}
