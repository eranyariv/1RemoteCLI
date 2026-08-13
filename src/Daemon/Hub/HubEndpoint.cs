namespace OneRemoteCli.Daemon.Hub;

/// <summary>
/// Where the agent looks for the relay.
/// <para>
/// One compiled-in default, overridable by an environment variable. There is no
/// config file on purpose: a hub address the user can mistype into a file they forget
/// about is a support burden, and the override exists mainly so a developer can point
/// a real agent at a local hub without editing anything.
/// </para>
/// </summary>
public static class HubEndpoint
{
    public const string EnvironmentVariable = "ONEREMOTE_HUB";

    /// <summary>Must match the path the hub maps <c>RelayHub</c> on.</summary>
    public const string Path = "hub";

    /// <summary>The deployed hub. See <c>docs/azure-setup.md</c>.</summary>
    public static readonly Uri Default = new("https://1remotecli-hub.azurewebsites.net");

    /// <summary>
    /// Resolves the full hub URL, accepting either a base address or one that already
    /// names the hub path — both are things a person reasonably types.
    /// </summary>
    public static Uri Resolve(string? configured = null)
    {
        string? value = configured ?? Environment.GetEnvironmentVariable(EnvironmentVariable);

        Uri baseUri = !string.IsNullOrWhiteSpace(value) &&
                      Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            ? parsed
            : Default;

        string path = baseUri.AbsolutePath.TrimEnd('/');

        return path.EndsWith('/' + Path, StringComparison.OrdinalIgnoreCase)
            ? baseUri
            : new Uri(baseUri, $"{path}/{Path}");
    }

    /// <summary>
    /// Where the phone app lives: the same origin, minus the hub path. The PWA is
    /// served by the hub rather than from a second host, so that a push deep link, an
    /// attach socket and a sign-in redirect all share one origin — three origins would
    /// mean three certificates and a CORS policy for no gain.
    /// </summary>
    public static Uri AppUri(string? configured = null)
    {
        Uri hub = Resolve(configured);

        return new Uri(hub.GetLeftPart(UriPartial.Authority));
    }
}
