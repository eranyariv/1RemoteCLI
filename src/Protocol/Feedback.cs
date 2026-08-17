namespace OneRemoteCli.Protocol;

/// <summary>
/// Where feedback goes, and what it is called when it arrives.
/// <para>
/// A mail link rather than a form or an issue tracker: the person who has something
/// to say is usually holding a phone, has just been surprised by something, and will
/// not sign up for anything. Mail is the one channel already installed.
/// </para>
/// <para>
/// The version is in the subject because it is the fact the reply always needs and
/// the one the sender is least likely to include.
/// </para>
/// </summary>
public static class Feedback
{
    public const string Address = "eran@yariv.org";

    public static string Subject => $"Feedback on 1RemoteCLI, version {ProductVersion.Current}";

    /// <summary>
    /// The whole link, ready to hand to the shell. The subject is percent-encoded:
    /// it contains a comma and spaces, and an unencoded query is a mail client's
    /// licence to keep whatever it feels like.
    /// </summary>
    public static string MailTo => $"mailto:{Address}?subject={Uri.EscapeDataString(Subject)}";
}
