namespace OneRemoteCli.Protocol.Tests;

public class FeedbackTests
{
    [Fact]
    public void TheSubjectNamesTheProductAndTheVersion()
    {
        Assert.Equal($"Feedback on 1RemoteCLI, version {ProductVersion.Current}", Feedback.Subject);
    }

    /// <summary>
    /// The comma and the spaces are the point. A mailto whose query is not encoded is
    /// handled differently by every mail client, and the one that drops the subject
    /// drops the version with it — which is the only reason the link exists.
    /// </summary>
    [Fact]
    public void TheLinkIsEncoded()
    {
        Assert.StartsWith("mailto:eran@yariv.org?subject=", Feedback.MailTo, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", Feedback.MailTo, StringComparison.Ordinal);
        Assert.Contains("Feedback%20on%201RemoteCLI%2C%20version%20", Feedback.MailTo, StringComparison.Ordinal);
    }
}
