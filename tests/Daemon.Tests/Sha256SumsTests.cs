using OneRemoteCli.Daemon.Update;

namespace OneRemoteCli.Daemon.Tests;

public class Sha256SumsTests
{
    private const string Hash = "9f2c1b8e7d6a5c4b3a2918f7e6d5c4b3a29180f7e6d5c4b3a2918f7e6d5c4b3a";

    [Fact]
    public void FindsTheAssetItWasAskedFor()
    {
        string contents = $"0000000000000000000000000000000000000000000000000000000000000000  other.zip\n{Hash}  1remote.exe\n";

        Assert.Equal(Hash, Sha256Sums.Find(contents, "1remote.exe"));
    }

    [Fact]
    public void DoesNotMindHowTheLinesEnd() =>
        Assert.Equal(Hash, Sha256Sums.Find($"{Hash}  1remote.exe\r\n", "1remote.exe"));

    [Fact]
    public void AcceptsTheBinaryModeStar() =>
        Assert.Equal(Hash, Sha256Sums.Find($"{Hash} *1remote.exe", "1remote.exe"));

    [Fact]
    public void MatchesTheNameWithoutRegardToCase() =>
        Assert.Equal(Hash, Sha256Sums.Find($"{Hash}  1Remote.EXE", "1remote.exe"));

    [Fact]
    public void LowercasesWhatItReturns() =>
        Assert.Equal(Hash, Sha256Sums.Find($"{Hash.ToUpperInvariant()}  1remote.exe", "1remote.exe"));

    [Fact]
    public void HasNothingToSayAboutAnAssetThatIsNotListed() =>
        Assert.Null(Sha256Sums.Find($"{Hash}  other.zip", "1remote.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasNothingToSayAboutAnEmptyFile(string? contents) => Assert.Null(Sha256Sums.Find(contents, "1remote.exe"));

    /// <summary>
    /// The failure this parser exists for. A download URL GitHub cannot resolve is
    /// answered with an HTML page and a 200, so "the checksums" can arrive looking like a
    /// file; if any of it parsed as a hash, the check would be decoration.
    /// </summary>
    [Fact]
    public void RefusesAnHtmlErrorPage()
    {
        const string page = """
            <!DOCTYPE html>
            <html lang="en"><head><title>Not Found</title></head>
            <body>1remote.exe not found</body></html>
            """;

        Assert.Null(Sha256Sums.Find(page, "1remote.exe"));
    }

    [Theory]
    [InlineData("notahash  1remote.exe")]
    [InlineData("9f2c1b8e7d6a5c4b3a2918f7e6d5c4b3a29180f7e6d5c4b3a2918f7e6d5c4b  1remote.exe")]
    [InlineData("9f2c1b8e7d6a5c4b3a2918f7e6d5c4b3a29180f7e6d5c4b3a2918f7e6d5c4b3azz  1remote.exe")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz  1remote.exe")]
    public void RefusesAFirstFieldThatIsNotSixtyFourHexCharacters(string line) =>
        Assert.Null(Sha256Sums.Find(line, "1remote.exe"));

    [Fact]
    public void RefusesALineWithTheWrongNumberOfFields() =>
        Assert.Null(Sha256Sums.Find($"{Hash}  1remote.exe extra", "1remote.exe"));

    [Fact]
    public void KeepsLookingPastABadLine() =>
        Assert.Equal(Hash, Sha256Sums.Find($"garbage\n\n{Hash}  1remote.exe", "1remote.exe"));

    [Fact]
    public void RefusesToBeAskedAboutNothing() =>
        Assert.Throws<ArgumentException>(() => Sha256Sums.Find($"{Hash}  1remote.exe", "  "));
}
