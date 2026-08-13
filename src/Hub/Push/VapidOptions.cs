namespace OneRemoteCli.Hub.Push;

/// <summary>
/// The VAPID keypair the hub signs push requests with, and the contact address it
/// gives the push services.
/// <para>
/// Absent by default, and absent is a supported state rather than a failure. The hub
/// runs unconfigured on every developer machine and in every test; refusing to start
/// without a keypair would mean nobody could work on the relay without first
/// provisioning secrets for a feature they are not touching. Push is simply off, and
/// says so once at startup.
/// </para>
/// <para>
/// The private key is configuration and never source. It arrives as an App Service
/// setting or from Key Vault, which is why there is no default and no sample value
/// anywhere in the repository - a placeholder keypair is exactly the kind of thing
/// that gets deployed.
/// </para>
/// </summary>
public sealed class VapidOptions
{
    public const string Section = "Push:Vapid";

    /// <summary>
    /// How a push service reaches a human about this application, as a <c>mailto:</c>
    /// or <c>https:</c> URL. Required by RFC 8292, and the services do use it when
    /// something is wrong at their end.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Base64url, uncompressed P-256 point. Public by definition - the browser is handed it.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Base64url P-256 private scalar. A secret, and the only one this feature has.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    public bool Configured =>
        !string.IsNullOrWhiteSpace(Subject) &&
        !string.IsNullOrWhiteSpace(PublicKey) &&
        !string.IsNullOrWhiteSpace(PrivateKey);
}
