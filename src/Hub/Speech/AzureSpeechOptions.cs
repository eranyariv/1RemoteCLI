using System.Text.RegularExpressions;

namespace OneRemoteCli.Hub.Speech;

/// <summary>
/// Server-held Azure AI Speech configuration for voice mode.
/// </summary>
public sealed partial class AzureSpeechOptions
{
    public const string Section = "AzureSpeech";

    /// <summary>The Azure Speech region, for example <c>eastus</c>.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// A Speech resource key supplied through App Service configuration or a Key Vault reference.
    /// It is exchanged server-side and is never returned to the PWA.
    /// </summary>
    public string SubscriptionKey { get; set; } = string.Empty;

    public string RecognitionLanguage { get; set; } = "en-US";

    public string VoiceName { get; set; } = "en-US-AvaMultilingualNeural";

    public bool Configured =>
        ValidRegion().IsMatch(Region) &&
        !string.IsNullOrWhiteSpace(SubscriptionKey) &&
        !string.IsNullOrWhiteSpace(RecognitionLanguage) &&
        !string.IsNullOrWhiteSpace(VoiceName);

    [GeneratedRegex("^[a-z0-9-]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidRegion();
}
