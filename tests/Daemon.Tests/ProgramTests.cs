using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The migration-safety rule from issue #174: the update restart blocker must count
/// ACP turns and reconnect-incapable terminal sessions, and nothing else, so v0.48
/// does not strand a pre-v0.48 wrapper while a later release can still restart
/// through wrappers that can reconnect.
/// </summary>
[SupportedOSPlatform("windows")]
public class ProgramTests
{
    [Fact]
    public void CountsAnAcpTurnEvenWithNoSessionsAtAll() =>
        Assert.Equal(1, Program.UpdateBlockerCount(1, []));

    [Fact]
    public void DoesNotCountATerminalSessionThatSupportsReconnect() =>
        Assert.Equal(0, Program.UpdateBlockerCount(0, [Session(supportsReconnect: true)]));

    /// <summary>
    /// The migration case itself: a wrapper from before this feature shipped never
    /// sets the flag, and that must still block a restart exactly as it always did.
    /// </summary>
    [Fact]
    public void CountsATerminalSessionThatDoesNotSupportReconnect() =>
        Assert.Equal(1, Program.UpdateBlockerCount(0, [Session(supportsReconnect: false)]));

    [Fact]
    public void AddsAcpTurnsAndIncapableSessionsTogether()
    {
        TerminalSession[] sessions =
        [
            Session(supportsReconnect: true),
            Session(supportsReconnect: false),
            Session(supportsReconnect: false),
        ];

        Assert.Equal(3, Program.UpdateBlockerCount(1, sessions));
    }

    private static TerminalSession Session(bool supportsReconnect) =>
        new(
            Guid.NewGuid().ToString("n"),
            "pwsh",
            [],
            @"C:\work",
            80,
            24,
            null,
            new NullChannel(),
            supportsReconnect: supportsReconnect);

    private sealed class NullChannel : ISessionChannel
    {
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendResizeAsync(int cols, int rows, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendInterruptAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
