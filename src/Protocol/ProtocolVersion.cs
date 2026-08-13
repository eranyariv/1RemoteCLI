namespace OneRemoteCli.Protocol;

/// <summary>
/// Wire protocol version, carried by <see cref="Hub.RegisterMachineRequest"/> and the
/// client handshake. The hub rejects anything it does not support with a clear
/// <see cref="Hub.ErrorNotification"/> rather than failing obscurely on the first
/// incompatible message.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>Version this build speaks and sends.</summary>
    public const int Current = 1;

    /// <summary>Oldest version this build still accepts from a peer.</summary>
    public const int MinimumSupported = 1;

    public static bool IsSupported(int version) =>
        version >= MinimumSupported && version <= Current;
}
