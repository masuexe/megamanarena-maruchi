namespace Mma.Protocol;

/// <summary>
/// Authoritative online mode, submode, and default stage configuration derived from Mega Man Arena 5.0.
/// </summary>
public sealed record GameModeConfig(
    string Mode,
    ushort OnlineMode,
    byte GameMode,
    byte Submode,
    byte Subsubmode,
    string DefaultStage,
    byte StockCount)
{
    public static IReadOnlyList<string> SupportedModes { get; } =
        ["Arena", "Teams", "Squads", "Boss", "Elevator", "Stage", "Arena8"];

    /// <summary>
    /// Resolves a mode name (case-insensitive) into its protocol configuration.
    /// </summary>
    public static GameModeConfig Resolve(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        string normalized = mode.Trim().ToLowerInvariant();
        return normalized switch
        {
            "arena" => new("Arena", MmaConstants.OnlineModeArena, MmaConstants.GameModeArena, MmaConstants.SubmodeArena, 0, "rmWilyBoss", MmaConstants.DefaultStockCount),
            "teams" => new("Teams", MmaConstants.OnlineModeArena, MmaConstants.GameModeArena, MmaConstants.SubmodeTeams, 0, "rmWilyBoss", MmaConstants.DefaultStockCount),
            "squads" => new("Squads", MmaConstants.OnlineModeSquads, MmaConstants.GameModeArena, MmaConstants.SubmodeSquads, 0, "rmWilyBoss", Math.Max(MmaConstants.DefaultStockCount, MmaConstants.SquadMinStockCount)),
            "boss" => new("Boss", MmaConstants.OnlineModeBoss, MmaConstants.GameModeBossOrStage, MmaConstants.SubmodeBoss, 0, "rmMechaDragon", MmaConstants.DefaultStockCount),
            "elevator" => new("Elevator", MmaConstants.OnlineModeElevator, MmaConstants.GameModeElevator, MmaConstants.SubmodeElevator, 0, "rmElevator", MmaConstants.DefaultStockCount),
            "stage" => new("Stage", MmaConstants.OnlineModeStage, MmaConstants.GameModeBossOrStage, MmaConstants.SubmodeStage, 0, "rmBombmanStageMode0", MmaConstants.DefaultStockCount),
            "arena8" => new("Arena8", MmaConstants.OnlineModeArena, MmaConstants.GameModeArena, MmaConstants.SubmodeArena, 0, "rmWilyBoss", MmaConstants.DefaultStockCount),
            _ => throw new ArgumentException($"Unsupported mode '{mode}'. Expected one of: {string.Join(", ", SupportedModes)}.", nameof(mode))
        };
    }

    public static bool TryResolve(string? mode, out GameModeConfig? config)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            config = null;
            return false;
        }

        try
        {
            config = Resolve(mode);
            return true;
        }
        catch (ArgumentException)
        {
            config = null;
            return false;
        }
    }

    public static bool RequiresArena8Protocol(int maxPlayers, string mode) =>
        maxPlayers > MmaConstants.StockMaxPlayers ||
        string.Equals(mode.Trim(), "Arena8", StringComparison.OrdinalIgnoreCase);

    public static string GetExpectedVersion(int maxPlayers, string mode) =>
        RequiresArena8Protocol(maxPlayers, mode)
            ? MmaConstants.Arena8Version
            : MmaConstants.StockVersion;

    public static string GetDefaultStage(string mode) => Resolve(mode).DefaultStage;
}
