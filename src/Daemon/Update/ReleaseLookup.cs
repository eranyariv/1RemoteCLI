using System.Net;

namespace OneRemoteCli.Daemon.Update;

/// <summary>
/// Asking github.com which release is current.
/// <para>
/// By following <c>/releases/latest</c>, not through the API. The API is limited to
/// sixty anonymous calls an hour per address, counted across everyone behind it, and
/// issue #102 is that limit being reached by strangers on a shared network — a check
/// that every agent performs on a timer is exactly the traffic that would make it
/// routine. The redirect target is the tag, needs no token, and has no allowance.
/// </para>
/// <para>
/// A HEAD request, because the answer is in the URL and the release page is several
/// hundred kilobytes of HTML nobody is going to read.
/// </para>
/// </summary>
public static class ReleaseLookup
{
    /// <summary>
    /// An <see cref="HttpClient"/> that does not follow redirects, since the redirect
    /// is the answer.
    /// <para>
    /// The user agent is set because github.com refuses requests without one, and the
    /// failure is a 403 that says nothing about the reason.
    /// </para>
    /// </summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            // Short. Nothing waits on this and a machine with no network should record a
            // failed check in seconds rather than sit on a socket for a minute.
            Timeout = TimeSpan.FromSeconds(20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("1RemoteCLI-agent");

        return client;
    }

    /// <summary>
    /// The tag of the latest release, or null when github.com did not point at one.
    /// </summary>
    public static async Task<string?> LatestTagAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var request = new HttpRequestMessage(HttpMethod.Head, ReleaseSource.LatestRelease);
        using HttpResponseMessage response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther))
        {
            return null;
        }

        Uri? location = response.Headers.Location;

        if (location is null)
        {
            return null;
        }

        // Relative in principle, and GitHub does send an absolute one — but a Location
        // that is relative would otherwise throw when its segments are read.
        if (!location.IsAbsoluteUri)
        {
            location = new Uri(ReleaseSource.LatestRelease, location);
        }

        return ReleaseSource.TagFromRedirect(location);
    }

    /// <summary>
    /// The download client, which does follow redirects: an asset URL redirects to the
    /// storage host the bytes actually live on.
    /// </summary>
    public static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient
        {
            // Generous, because this is thirty megabytes over whatever connection the
            // machine has.
            Timeout = TimeSpan.FromMinutes(10),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("1RemoteCLI-agent");

        return client;
    }
}
