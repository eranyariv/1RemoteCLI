using System.Reflection;

namespace OneRemoteCli.Protocol;

/// <summary>
/// The release version, as a person reads it: <c>x.yy</c>, starting at <c>0.01</c>.
/// <para>
/// One number for the whole product. The agent, the hub and the PWA ship together and
/// are only ever meaningful as a set, so a user reporting a problem should not have to
/// be asked which of three versions they meant. It is stamped from the repository's
/// <c>VERSION</c> file at build time — see <c>Directory.Build.props</c>.
/// </para>
/// <para>
/// Read from this assembly rather than the caller's, because every assembly in the
/// solution carries the same stamp and asking the entry assembly would give a test
/// host's version when a test asks.
/// </para>
/// </summary>
public static class ProductVersion
{
    /// <summary>
    /// The version to show. Falls back to the numeric assembly version if the build
    /// was not stamped, which is wrong but is at least a number rather than a crash.
    /// </summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        Assembly assembly = typeof(ProductVersion).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "0.00"
            : informational;
    }
}
