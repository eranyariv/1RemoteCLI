using Microsoft.Extensions.Logging.Abstractions;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

public sealed class TerminalUploadTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"1remote-terminal-uploads-{Guid.NewGuid():n}");

    [Fact]
    public void WritesBytesUnderASanitizedUniqueSessionPath()
    {
        var registry = Registry();
        TerminalSession session = AddSession(registry);
        string uploadId = Guid.NewGuid().ToString();

        TerminalUploadReply begun = registry.BeginUpload(new BeginTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            FileName = @"..\../folder/bad:name?.png",
            TotalBytes = 5,
        });
        Assert.Null(begun.ErrorCode);

        TerminalUploadReply first = registry.AppendUpload(new TerminalUploadChunkNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            Offset = 0,
            Data = [1, 2],
        });
        Assert.Equal(2, first.ConfirmedBytes);
        Assert.Null(first.RemotePath);

        TerminalUploadReply completed = registry.AppendUpload(new TerminalUploadChunkNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            Offset = 2,
            Data = [3, 4, 5],
        });

        string path = Assert.IsType<string>(completed.RemotePath);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(path));
        Assert.StartsWith(
            Path.GetFullPath(_root) + Path.DirectorySeparatorChar,
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bad_name_.png", Path.GetFileName(path));
        Assert.DoesNotContain("..", Path.GetRelativePath(_root, path), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesOversizedFilesBeforeCreatingAnything()
    {
        var registry = Registry();
        TerminalSession session = AddSession(registry);

        TerminalUploadReply reply = registry.BeginUpload(new BeginTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = Guid.NewGuid().ToString(),
            FileName = "too-large.bin",
            TotalBytes = TerminalUploadLimits.MaxFileBytes + 1,
        });

        Assert.Equal(ErrorCodes.FileTooLarge, reply.ErrorCode);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void EnforcesChunkOwnershipAndRemovesCancelledPartialBytes()
    {
        var registry = Registry();
        TerminalSession session = AddSession(registry);
        string uploadId = Guid.NewGuid().ToString();

        registry.BeginUpload(new BeginTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            FileName = "notes.txt",
            TotalBytes = 4,
        });

        TerminalUploadReply stolen = registry.AppendUpload(new TerminalUploadChunkNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-2",
            UploadId = uploadId,
            Offset = 0,
            Data = [1],
        });
        Assert.Equal(ErrorCodes.UploadNotFound, stolen.ErrorCode);

        registry.AppendUpload(new TerminalUploadChunkNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            Offset = 0,
            Data = [1, 2],
        });

        TerminalUploadReply cancelled = registry.CancelUpload(new CancelTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
        });

        Assert.Equal(ErrorCodes.UploadCancelled, cancelled.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void SessionCloseDeletesCompletedAttachments()
    {
        var registry = Registry();
        TerminalSession session = AddSession(registry);
        string uploadId = Guid.NewGuid().ToString();

        TerminalUploadReply completed = registry.BeginUpload(new BeginTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            FileName = "empty.txt",
            TotalBytes = 0,
        });

        Assert.True(File.Exists(completed.RemotePath));
        Assert.True(registry.Remove(session.SessionId));
        Assert.False(File.Exists(completed.RemotePath));
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public void RepeatedFileNamesUseDifferentPathsAndNeverOverwrite()
    {
        var registry = Registry();
        TerminalSession session = AddSession(registry);

        string first = Upload(registry, session, "photo.jpg", [1]);
        string second = Upload(registry, session, "photo.jpg", [2]);

        Assert.NotEqual(first, second);
        Assert.Equal([1], File.ReadAllBytes(first));
        Assert.Equal([2], File.ReadAllBytes(second));
    }

    [Fact]
    public void CancelRemovesAFileThatCompletedWhileTheRequestWasInFlight()
    {
        var registry = Registry();
        TerminalSession session = AddSession(registry);
        string uploadId = Guid.NewGuid().ToString();
        const string client = "phone-1";

        registry.BeginUpload(new BeginTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = client,
            UploadId = uploadId,
            FileName = "photo.jpg",
            TotalBytes = 1,
        });
        TerminalUploadReply completed = registry.AppendUpload(new TerminalUploadChunkNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = client,
            UploadId = uploadId,
            Offset = 0,
            Data = [1],
        });

        TerminalUploadReply cancelled = registry.CancelUpload(new CancelTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = client,
            UploadId = uploadId,
        });

        Assert.Equal(ErrorCodes.UploadCancelled, cancelled.ErrorCode);
        Assert.False(File.Exists(completed.RemotePath));
    }

    [Fact]
    public void SanitizesCmdExpansionCharactersFromFileNames()
    {
        var registry = Registry();
        TerminalSession session = AddSession(registry);

        string path = Upload(registry, session, "%PATH%!PHOTO!.jpg", [1]);

        Assert.Equal("_PATH__PHOTO_.jpg", Path.GetFileName(path));
    }

    [Fact]
    public void StartupDeletesOnlyDirectoriesPastTheRetentionWindow()
    {
        DateTimeOffset now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        string stale = Path.Combine(_root, "stale");
        string fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        Directory.SetLastWriteTimeUtc(stale, (now - TerminalUploadStore.StaleRetention - TimeSpan.FromMinutes(1)).UtcDateTime);
        Directory.SetLastWriteTimeUtc(fresh, (now - TimeSpan.FromHours(1)).UtcDateTime);

        _ = Registry(new FixedTimeProvider(now));

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void HourlySweepBoundsRetentionWithoutAnotherRestart()
    {
        DateTimeOffset now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        string orphan = Path.Combine(_root, "orphan");
        Directory.CreateDirectory(orphan);
        Directory.SetLastWriteTimeUtc(orphan, now.UtcDateTime);
        var time = new ManualTimeProvider(now);
        using SessionRegistry registry = Registry(time);

        time.Advance(TerminalUploadStore.StaleRetention + TerminalUploadStore.StaleSweepInterval);
        time.FireTimer();

        Assert.False(Directory.Exists(orphan));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionRegistry Registry(TimeProvider? time = null) =>
        new(_root, time, NullLogger.Instance);

    private static TerminalSession AddSession(SessionRegistry registry) =>
        registry.Add("pwsh", [], @"C:\Work", 80, 24, null, new NullChannel());

    private static string Upload(
        SessionRegistry registry,
        TerminalSession session,
        string fileName,
        byte[] data)
    {
        string uploadId = Guid.NewGuid().ToString();
        registry.BeginUpload(new BeginTerminalUploadNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            FileName = fileName,
            TotalBytes = data.LongLength,
        });

        TerminalUploadReply completed = registry.AppendUpload(new TerminalUploadChunkNotification
        {
            SessionId = session.SessionId,
            ClientConnectionId = "phone-1",
            UploadId = uploadId,
            Offset = 0,
            Data = data,
        });

        return Assert.IsType<string>(completed.RemotePath);
    }

    private sealed class NullChannel : ISessionChannel
    {
        public ValueTask SendInputAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SendResizeAsync(
            int cols,
            int rows,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SendInterruptAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new ManualTimer(callback, state);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private ManualTimer? _timer;

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _timer = new ManualTimer(callback, state);
            return _timer;
        }

        public void Advance(TimeSpan elapsed) => now += elapsed;

        public void FireTimer() => _timer!.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

        public void Fire()
        {
            if (!_disposed)
            {
                callback(state);
            }
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
