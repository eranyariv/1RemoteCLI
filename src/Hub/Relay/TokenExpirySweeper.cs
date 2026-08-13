using Microsoft.AspNetCore.SignalR;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Relay;

/// <summary>
/// Asks connections to refresh before their token runs out, and drops the ones that
/// do not.
/// <para>
/// Deliberately thin: everything worth testing lives in
/// <see cref="ConnectionTokens.Sweep"/>, which is a pure-ish function of the clock, so
/// the interesting cases are covered without waiting on a real timer.
/// </para>
/// </summary>
public sealed class TokenExpirySweeper(
    ConnectionTokens tokens,
    IHubContext<RelayHub> hub,
    TimeProvider time,
    ILogger<TokenExpirySweeper> logger) : BackgroundService
{
    /// <summary>
    /// How often to look.
    /// <para>
    /// Thirty seconds against a five-minute warning window: the holder gets at least
    /// four and a half minutes' notice in the worst case, and the sweep itself is a
    /// walk over a dictionary with one entry per live connection.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ConnectionTokens _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly IHubContext<RelayHub> _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<TokenExpirySweeper> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
                await SweepAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // A sweep that throws must not take the sweeper with it: the next one
                // would be the thing that ends an expired connection.
                _logger.LogError(error, "Token expiry sweep failed.");
            }
        }
    }

    /// <summary>One pass. Public so a test can drive it without a clock.</summary>
    public async Task SweepAsync()
    {
        foreach (string connectionId in _tokens.Sweep())
        {
            DateTimeOffset? expiresAt = _tokens.ExpiryOf(connectionId);

            if (expiresAt is null) continue;

            await _hub.Clients.Client(connectionId).SendAsync(
                HubMethods.Client.TokenExpiring,
                new TokenExpiringNotification { ExpiresAt = expiresAt.Value }).ConfigureAwait(false);
        }
    }
}
