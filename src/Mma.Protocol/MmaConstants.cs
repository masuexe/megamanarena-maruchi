namespace Mma.Protocol;

/// <summary>
/// Shared constants for Mega Man Arena headless server and client patches.
/// </summary>
public static class MmaConstants
{
    public const int DefaultPort = 6520;
    public const int Arena8Port = 6528;

    public const int MinPlayers = 2;
    public const int StockMaxPlayers = 4;
    public const int DefaultMaxPlayers = 8;
    public const int MaxSupportedPlayers = 64;

    public const byte DefaultBuffer = 6;
    public const byte MaxOnlineBuffer = 12;
    public const byte DefaultStockCount = 4;
    public const byte SquadMinStockCount = 2;
    public const byte DefaultHealthMax = 28;
    public const ushort DefaultStageSelection = 839;

    public const string DefaultMode = "Arena";
    public const string ProbePing = "MMA_PING";
    public const string ProbePongPrefix = "MMA_PONG";

    public const string StockVersion = "5.0.0.4890";
    public const string Arena8Version = "5.0.0.4890-arena8";

    public const ushort OnlineModeArena = 810;
    public const ushort OnlineModeSquads = 812;
    public const ushort OnlineModeBoss = 813;
    public const ushort OnlineModeElevator = 8002;
    public const ushort OnlineModeStage = 823;
    public const ushort OnlineModeTourney = 844;

    public const byte GameModeArena = 1;
    public const byte GameModeBossOrStage = 3;
    public const byte GameModeElevator = 5;

    public const byte SubmodeSquads = 1;
    public const byte SubmodeTeams = 2;
    public const byte SubmodeArena = 3;
    public const byte SubmodeElevator = 8;
    public const byte SubmodeStage = 11;
    public const byte SubmodeBoss = 12;

    public const ushort EmptySquadSentinel = 9999;

    public const int StartingMatchTimeoutSeconds = 10;
    public const int MatchStallTimeoutSeconds = 15;
}
