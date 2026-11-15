namespace Mma.Protocol;

/// <summary>
/// Known GameMaker UDP packet type identifiers observed in Mega Man Arena 5.0.
/// </summary>
public enum MmaPacketType : byte
{
    Input = 0,
    Connect = 1,
    ConnectAck = 2,
    Ping = 3,
    NewPlayerWelcome = 4,
    NewPlayerData = 5,
    CharChange = 6,
    StageChange = 7,
    LobbyChange = 8,
    ChangePlayerPort = 9,
    ChangePlayerData = 10,
    SquadList = 11,
    RulesUpdate = 12,
    BufferChange = 13,
    PlayerReady = 14,
    GotoLevel = 15,
    PortDiscovery = 17,
    PortDiscoveryAck = 18,
    DisallowConnect = 19,
    Disconnect = 20,
    PlayerUnready = 21,
    NewPlayerJoinAck = 22,
    GratuitousPing = 23,
    GratuitousPingEcho = 24,
    LobbyClosed = 25,
    InputPing = 26,
    SquadResend = 27,
    Forfeit = 28,
    MatchForfeit = 29,
    KickPlayer = 30,
    Handicap = 31,
    TourneyRules = 32,
    TourneyStrikeTurn = 33,
    TourneyStrike = 34,
    TourneyTimer = 35,
    ObserverPing = 36,
    ObserverDisconnect = 37,
    ObserverCount = 38,
    StageDifficulties = 39
}
