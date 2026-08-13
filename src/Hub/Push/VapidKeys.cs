using System.Security.Cryptography;

namespace OneRemoteCli.Hub.Push;

/// <summary>
/// Generating the keypair that identifies this hub to the push services.
/// <para>
/// A one-off setup chore, but not a trivial one to get right. The public key must be
/// the <em>uncompressed</em> P-256 point — <c>0x04</c> then X then Y, 65 bytes — and
/// both halves must be base64url with the padding stripped. A padded key, or a
/// compressed point, is accepted by every tool that handles it and then produces a
/// subscription the browser refuses, with nothing on the server to say why. Doing it
/// here means it is done once, and testable.
/// </para>
/// </summary>
public static class VapidKeys
{
    /// <summary>A fresh keypair, as the two values the hub's configuration wants.</summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(includePrivateParameters: true);

        byte[] point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);

        return (Encode(point), Encode(parameters.D!));
    }

    /// <summary>base64url, unpadded — the only encoding Web Push accepts.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
