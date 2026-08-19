using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>The relay-facing half of an ACP provider.</summary>
public interface IAgentChatSink
{
    ValueTask OnChatOpenedAsync(AcpSession session, CancellationToken cancellationToken = default);

    ValueTask OnChatUpdatedAsync(AcpSession session, CancellationToken cancellationToken = default);

    ValueTask OnChatClosedAsync(AcpSession session, CancellationToken cancellationToken = default);

    ValueTask OnChatTranscriptAsync(
        AcpSession session,
        ChatTranscriptKind kind,
        ChatEvent[] events,
        string? targetConnectionId = null,
        CancellationToken cancellationToken = default);

    ValueTask OnChatAttentionAsync(
        AcpSession session,
        bool awaitingInput,
        string? hint,
        CancellationToken cancellationToken = default);
}
