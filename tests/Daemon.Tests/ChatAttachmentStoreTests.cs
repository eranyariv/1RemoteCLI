using OneRemoteCli.Daemon.Chat;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The chat attachment staging store: ownership, ordering, budgets, and above all
/// that nothing is left on disk once an attachment stops being sendable.
/// </summary>
public sealed class ChatAttachmentStoreTests : IDisposable
{
    private const string Session = "chat-1";
    private const string Client = "phone-1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"1remote-chat-attachments-{Guid.NewGuid():n}");

    [Fact]
    public void StagesOrderedChunksUnderASanitizedSessionScopedPath()
    {
        using var store = new ChatAttachmentStore(_root);
        string attachmentId = Guid.NewGuid().ToString();

        ChatAttachmentReply begun = store.Begin(Begin(attachmentId, @"..\../pics/bad:name?.png", 5));
        Assert.Null(begun.ErrorCode);
        Assert.False(begun.Completed);

        ChatAttachmentReply first = store.Append(Chunk(attachmentId, 0, [1, 2]));
        Assert.Equal(2, first.ConfirmedBytes);
        Assert.False(first.Completed);

        ChatAttachmentReply done = store.Append(Chunk(attachmentId, 2, [3, 4, 5]));
        Assert.True(done.Completed);
        Assert.Equal(5, done.ConfirmedBytes);

        string path = Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal("bad_name_.png", Path.GetFileName(path));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(path));
        Assert.DoesNotContain("..", Path.GetRelativePath(_root, path), StringComparison.Ordinal);

        Assert.True(store.TryRead(
            Session,
            Client,
            [attachmentId],
            out IReadOnlyList<ChatAttachmentContent> contents,
            out ChatPromptReply? error));
        Assert.Null(error);
        Assert.Equal([1, 2, 3, 4, 5], Assert.Single(contents).Bytes);
    }

    [Fact]
    public void RefusesOutOfOrderChunksAndAnotherClientsAttachment()
    {
        using var store = new ChatAttachmentStore(_root);
        string attachmentId = Guid.NewGuid().ToString();
        store.Begin(Begin(attachmentId, "notes.txt", 4));

        ChatAttachmentChunkNotification stolen = Chunk(attachmentId, 0, [1]);
        stolen.ClientConnectionId = "phone-2";
        Assert.Equal(ErrorCodes.AttachmentNotFound, store.Append(stolen).ErrorCode);

        Assert.Equal(
            ErrorCodes.InvalidRequest,
            store.Append(Chunk(attachmentId, 2, [1, 2])).ErrorCode);

        store.Append(Chunk(attachmentId, 0, [1, 2]));
        Assert.Equal(
            ErrorCodes.InvalidRequest,
            store.Append(Chunk(attachmentId, 0, [1, 2])).ErrorCode);
    }

    [Fact]
    public void RefusesOversizedAndOverBudgetSelections()
    {
        using var store = new ChatAttachmentStore(_root);

        Assert.Equal(
            ErrorCodes.AttachmentTooLarge,
            store.Begin(Begin(
                Guid.NewGuid().ToString(),
                "huge.bin",
                ChatAttachmentLimits.MaxAttachmentBytes + 1)).ErrorCode);
        Assert.False(Directory.Exists(_root));

        for (int index = 0; index < ChatAttachmentLimits.MaxAttachmentCount; index++)
        {
            Assert.Null(store.Begin(Begin(Guid.NewGuid().ToString(), $"file-{index}.txt", 1)).ErrorCode);
        }

        Assert.Equal(
            ErrorCodes.AttachmentBudgetExceeded,
            store.Begin(Begin(Guid.NewGuid().ToString(), "one-too-many.txt", 1)).ErrorCode);
    }

    [Fact]
    public void RefusesAnAggregateLargerThanOnePromptMayCarry()
    {
        using var store = new ChatAttachmentStore(_root);
        long each = ChatAttachmentLimits.MaxAttachmentBytes;
        int fitting = (int)(ChatAttachmentLimits.MaxPromptBytes / each);

        for (int index = 0; index < fitting; index++)
        {
            Assert.Null(store.Begin(Begin(Guid.NewGuid().ToString(), $"photo-{index}.png", each)).ErrorCode);
        }

        Assert.Equal(
            ErrorCodes.AttachmentBudgetExceeded,
            store.Begin(Begin(Guid.NewGuid().ToString(), "over.png", each)).ErrorCode);
    }

    [Fact]
    public void IncompleteAttachmentsAreNeverReadableByAPrompt()
    {
        using var store = new ChatAttachmentStore(_root);
        string attachmentId = Guid.NewGuid().ToString();
        store.Begin(Begin(attachmentId, "notes.txt", 4));
        store.Append(Chunk(attachmentId, 0, [1, 2]));

        Assert.False(store.TryRead(Session, Client, [attachmentId], out _, out ChatPromptReply? error));
        Assert.Equal(ErrorCodes.AttachmentNotFound, error!.ErrorCode);
    }

    [Fact]
    public void APromptCannotReadAnotherClientsCompletedAttachment()
    {
        using var store = new ChatAttachmentStore(_root);
        string attachmentId = Complete(store, "receipt.png", [1, 2, 3]);

        Assert.False(store.TryRead(Session, "phone-2", [attachmentId], out _, out ChatPromptReply? stolen));
        Assert.Equal(ErrorCodes.AttachmentNotFound, stolen!.ErrorCode);

        Assert.False(store.TryRead("chat-2", Client, [attachmentId], out _, out ChatPromptReply? crossed));
        Assert.Equal(ErrorCodes.AttachmentNotFound, crossed!.ErrorCode);
    }

    [Fact]
    public void ReadingDoesNotConsume_ButConsumingDeletes()
    {
        using var store = new ChatAttachmentStore(_root);
        string attachmentId = Complete(store, "receipt.png", [1, 2, 3]);

        Assert.True(store.TryRead(Session, Client, [attachmentId], out _, out _));

        // Read twice on purpose: a prompt the ACP agent refused must leave the user's
        // selection intact and re-sendable.
        Assert.True(store.TryRead(Session, Client, [attachmentId], out _, out _));

        store.Consume(Session, Client, [attachmentId]);

        Assert.False(store.TryRead(Session, Client, [attachmentId], out _, out _));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CancellingACompletedAttachmentDeletesIt()
    {
        using var store = new ChatAttachmentStore(_root);
        string attachmentId = Complete(store, "receipt.png", [1, 2, 3]);

        ChatAttachmentReply cancelled = store.Cancel(new CancelChatAttachmentNotification
        {
            SessionId = Session,
            ClientConnectionId = Client,
            AttachmentId = attachmentId,
        });

        Assert.Equal(ErrorCodes.AttachmentCancelled, cancelled.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void DetachingASessionOrLosingTheRelayRemovesStagedBytes()
    {
        using var store = new ChatAttachmentStore(_root);
        string mine = Complete(store, "mine.png", [1]);
        string theirs = Guid.NewGuid().ToString();
        ChatAttachmentReply begun = store.Begin(new BeginChatAttachmentNotification
        {
            SessionId = Session,
            ClientConnectionId = "phone-2",
            AttachmentId = theirs,
            FileName = "theirs.png",
            MimeType = "image/png",
            TotalBytes = 2,
        });
        Assert.Null(begun.ErrorCode);

        store.RemoveForClient(Session, Client);
        Assert.False(store.TryRead(Session, Client, [mine], out _, out _));
        Assert.NotEmpty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));

        store.RemoveAll();
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.False(store.TryRead(Session, "phone-2", [theirs], out _, out _));
    }

    [Fact]
    public void ASessionThatDisappearsTakesItsDirectoryWithIt()
    {
        using var store = new ChatAttachmentStore(_root);
        Complete(store, "receipt.png", [1, 2]);

        store.RemoveSession(Session);

        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    private static string Complete(ChatAttachmentStore store, string fileName, byte[] bytes)
    {
        string attachmentId = Guid.NewGuid().ToString();
        Assert.Null(store.Begin(Begin(attachmentId, fileName, bytes.Length)).ErrorCode);
        Assert.True(store.Append(Chunk(attachmentId, 0, bytes)).Completed);
        return attachmentId;
    }

    private static BeginChatAttachmentNotification Begin(
        string attachmentId,
        string fileName,
        long totalBytes) =>
        new()
        {
            SessionId = Session,
            ClientConnectionId = Client,
            AttachmentId = attachmentId,
            FileName = fileName,
            MimeType = "application/octet-stream",
            TotalBytes = totalBytes,
        };

    private static ChatAttachmentChunkNotification Chunk(
        string attachmentId,
        long offset,
        byte[] data) =>
        new()
        {
            SessionId = Session,
            ClientConnectionId = Client,
            AttachmentId = attachmentId,
            Offset = offset,
            Data = data,
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
