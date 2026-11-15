namespace Mma.Protocol;

/// <summary>
/// Known GameMaker UDP packet type identifiers observed in Mega Man Arena 5.0 patches.
/// </summary>
public enum MmaPacketType : byte
{
    Connect = 1,
    NewPlayerWelcome = 4,
    CharChange = 6,
    PlayerReady = 14,
    PlayerUnready = 21,
    LobbyClosed = 25,
    Forfeit = 28
}
