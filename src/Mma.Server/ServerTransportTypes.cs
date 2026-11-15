using System.Net;

namespace Mma.Server;

/// <summary>
/// Selects which of the two bound server UDP sockets sends an outbound datagram.
/// </summary>
public enum UdpTransportSocket
{
    Control = 0,
    Input = 1
}

/// <summary>
/// Immutable outbound UDP datagram produced by <see cref="MmaServerEngine"/>.
/// </summary>
public readonly record struct OutboundDatagram(
    UdpTransportSocket Socket,
    IPEndPoint Target,
    byte[] Payload);

/// <summary>
/// Lifecycle phase of the single headless lobby room.
/// </summary>
public enum LobbyPhase
{
    Lobby = 0,
    StartingMatch = 1,
    InMatch = 2
}
