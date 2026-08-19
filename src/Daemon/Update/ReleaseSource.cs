using System.Runtime.InteropServices;

namespace OneRemoteCli.Daemon.Update;

/// <summary>
/// Where releases live and what they are called.
/// <para>
/// The same names <c>scripts/install.ps1</c> uses, because an agent that updated
/// itself to a differently-named asset than the installer fetches would be a second
/// distribution channel nobody was maintaining.
/// </para>
/// </summary>
public static class ReleaseSource
{
    public const string Repository = "eranyariv/1RemoteCLI";

    /// <summary>The file whose hashes every asset in a release is checked against.</summary>
    public const string ChecksumsAsset = "SHA256SUMS.txt";

    /// <summary>
    /// The asset for the architecture this process is running as — not the machine's.
    /// <para>
    /// An x64 build running under emulation on an Arm machine must stay x64: replacing
    /// it with the Arm asset would leave the wrapped sessions, which the agent starts
    /// as copies of itself, unable to load the same native pseudoconsole bits the
    /// running process already proved it could.
    /// </para>
    /// <para>
    /// The names are the ones the release workflow publishes and
    /// <c>scripts/install.ps1</c> fetches — <c>1remote-win-x64.exe</c>, the runtime
    /// identifier and not just the architecture. Getting this wrong does not fail
    /// loudly: the release simply appears not to list an asset for this machine, and
    /// every check would report "there is nothing to check a download against" forever.
    /// The tests pin these names against the release workflow for that reason.
    /// </para>
    /// </summary>
    public static string AssetName => AssetFor(RuntimeInformation.ProcessArchitecture);

    public static string AssetFor(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => "1remote-win-arm64.exe",
        _ => "1remote-win-x64.exe",
    };

    /// <summary>
    /// Where <c>/releases/latest</c> lives.
    /// <para>
    /// The page rather than the API. The API is rate-limited to sixty anonymous calls
    /// an hour per address, counted across everyone behind it, and issue #102 is that
    /// allowance being exhausted by strangers on an office network. Every agent on
    /// that network checking once a day would be the same problem again, made worse by
    /// being automatic. Following the redirect needs no API and has no allowance.
    /// </para>
    /// </summary>
    public static Uri LatestRelease { get; } = new($"https://github.com/{Repository}/releases/latest");

    /// <summary>The plain download URL for one asset of one tag.</summary>
    public static Uri Download(string tag, string asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);

        return new Uri($"https://github.com/{Repository}/releases/download/{tag}/{asset}");
    }

    /// <summary>
    /// The tag <c>/releases/latest</c> redirected to, or null when the answer was not
    /// a release.
    /// <para>
    /// A repository with no releases redirects nowhere, leaving the URL that was asked
    /// for and a last segment of <c>latest</c> — which is not a tag, and would produce
    /// a download URL that 404s.
    /// </para>
    /// </summary>
    public static string? TagFromRedirect(Uri? landed)
    {
        if (landed is null)
        {
            return null;
        }

        string tag = landed.AbsoluteUri.TrimEnd('/').Split('/')[^1];

        return tag.Length == 0 || string.Equals(tag, "latest", StringComparison.Ordinal) ? null : tag;
    }
}
