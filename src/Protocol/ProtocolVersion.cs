namespace OneRemoteCli.Protocol;

/// <summary>
/// Wire protocol version, carried by <see cref="Hub.RegisterMachineRequest"/> and the
/// client handshake. The hub rejects anything it does not support with a clear
/// <see cref="Hub.ErrorNotification"/> rather than failing obscurely on the first
/// incompatible message.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>
    /// Version this build speaks and sends.
    /// <para>
    /// 2 added <c>SetSessionType</c> and <c>SessionUpdated</c>, and appended
    /// <see cref="Hub.SessionInfo.CliType"/>. Both are additive: a version 1 peer
    /// never invokes the new methods, and its decoder stops reading before the new
    /// field. That is why <see cref="MinimumSupported"/> did not move with it.
    /// </para>
    /// </summary>
    public const int Current = 2;

    /// <summary>Oldest version this build still accepts from a peer.</summary>
    public const int MinimumSupported = 1;

    public static bool IsSupported(int version) =>
        version >= MinimumSupported && version <= Current;
}
