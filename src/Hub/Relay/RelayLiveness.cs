using Microsoft.AspNetCore.SignalR;

namespace OneRemoteCli.Hub.Relay;

/// <summary>
/// How quickly a connection that has gone quiet is declared dead.
/// <para>
/// Shared by the application and its tests rather than written out twice, because a
/// harness that quietly used SignalR's defaults would be testing a hub that behaves
/// differently from the deployed one on exactly the axis these numbers control.
/// </para>
/// </summary>
public static class RelayLiveness
{
    /// <summary>
    /// How often the hub pings an idle connection.
    /// <para>
    /// Terminal output is chatty but small, and a phone on a flaky connection is the
    /// normal case rather than the exception, so liveness is measured in seconds.
    /// </para>
    /// </summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long silence is tolerated before the connection is dropped.
    /// <para>
    /// Two keep-alive intervals, which is the documented ratio: one missed ping is a
    /// blip, two is a connection that is gone. This is what puts an upper bound on how
    /// long a machine can look online after its agent has died — the phone would
    /// otherwise offer sessions that nothing is listening to.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);

    public static void Apply(HubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.KeepAliveInterval = KeepAlive;
        options.ClientTimeoutInterval = ClientTimeout;
    }
}
