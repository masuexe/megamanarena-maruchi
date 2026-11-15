namespace Mma.Protocol;

/// <summary>
/// Rejection codes returned in <see cref="MmaPacketType.DisallowConnect"/> packets.
/// </summary>
public enum DisallowConnectReason : byte
{
    LobbyFull = 0,
    VersionMismatch = 1,
    MatchAlreadyStarted = 2,
    ObserversDisabled = 3,
    ObserverMaxReached = 4
}
