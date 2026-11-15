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

    public const string DefaultMode = "Arena";
    public const string ProbePing = "MMA_PING";
    public const string ProbePongPrefix = "MMA_PONG";
}
