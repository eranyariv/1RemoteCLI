using System.Text.RegularExpressions;

namespace OneRemoteCli.Protocol.Tests;

public class ProductVersionTests
{
    /// <summary>
    /// The stamp is applied by Directory.Build.props, which is exactly the kind of
    /// thing that silently stops happening: a project that opts out, an SDK that
    /// stops writing the attribute, a VERSION file edited to something that is not
    /// a version. Any of those leaves the tray and the PWA showing a number that is
    /// not the release, and nothing else would notice.
    /// </summary>
    [Fact]
    public void TheVersionIsTheDisplayFormAndNotTheAssemblyVersion()
    {
        Assert.Matches(new Regex(@"^\d+\.\d{2}$"), ProductVersion.Current);
    }

    [Fact]
    public void TheNumericVersionIsTheSameNumberInTheFormTheToolingAccepts()
    {
        // 0.01 -> 0.1.0. Not decoration: NuGet and the assembly metadata reject
        // `0.01`, so the build derives one from the other, and a derivation nothing
        // checks is one that can quietly start producing 0.0.0.
        string[] parts = ProductVersion.Current.Split('.');
        Version? numeric = typeof(ProductVersion).Assembly.GetName().Version;

        Assert.NotNull(numeric);
        Assert.Equal(int.Parse(parts[0]), numeric.Major);
        Assert.Equal(int.Parse(parts[1]), numeric.Minor);
    }
}
