using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Mma.Protocol;

namespace Mma.Server;

/// <summary>
/// Runtime configuration for the headless UDP server, resolved from defaults, JSON config, environment variables, and CLI arguments.
/// </summary>
public sealed record ServerOptions(
    IPAddress BindAddress,
    int Port,
    int MaxPlayers,
    string Mode,
    string? Stage = null,
    int Buffer = MmaConstants.DefaultBuffer)
{
    public const int DefaultParsedMaxPlayers = MmaConstants.StockMaxPlayers;
    public const int DefaultParsedBuffer = 3;

    private static readonly JsonDocumentOptions ConfigJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public GameModeConfig ModeConfig => GameModeConfig.Resolve(Mode);

    public string ResolvedStage => !string.IsNullOrWhiteSpace(Stage) ? Stage.Trim() : ModeConfig.DefaultStage;

    public bool UsesArena8Protocol => GameModeConfig.RequiresArena8Protocol(MaxPlayers, Mode);

    public string ExpectedVersion => GameModeConfig.GetExpectedVersion(MaxPlayers, Mode);

    public static bool IsHelpRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string GetHelpText() =>
        """
        Mega Man Arena Headless Server (UDP)

        Usage:
          Mma.Server [options]

        CLI Options (highest precedence):
          --config <path>        Path to server.json configuration file
          --bind <ipv4>          IPv4 address to bind (default: 0.0.0.0)
          --port <0..65534>      Control UDP port; also binds Port + 1 (default: 6520)
          --max-players <2..64>  Player slots; <=4 uses Stock4P unless Mode=Arena8 (default: 4)
          --mode <mode>          Arena, Teams, Squads, Boss, Elevator, Stage, Arena8 (default: Arena)
          --stage <room>         Optional stage room override (default: mode default)
          --buffer <0..12>       Online input buffer frames (default: 3)
          --help, -h, /?         Show this help text and exit

        Configuration Precedence (lowest to highest):
          1. Built-in defaults (Bind=0.0.0.0, Port=6520, MaxPlayers=4, Mode=Arena, Buffer=3)
          2. server.json (resolved via --config > MMA_CONFIG > ./server.json > <exeDir>/server.json)
             Keys (case-insensitive): Bind, Port, MaxPlayers (or Players), Mode, Stage, Buffer
          3. Environment variables (non-whitespace):
             MMA_CONFIG, MMA_BIND, MMA_PORT, MMA_MAX_PLAYERS, MMA_MODE, MMA_STAGE, MMA_BUFFER
          4. CLI flags

        Firewall Note:
          The server binds TWO consecutive UDP ports: Port (control/RUDP) and Port + 1 (fast input).
          Open both UDP ports (e.g. 6520-6521 for Stock 4P, or 6528-6529 for Arena8).
        """;

    public static string? ResolveDefaultConfigPath(string? currentDirectory = null, string? baseDirectory = null)
    {
        string? cwd = currentDirectory ?? Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            string cwdCandidate = Path.Combine(cwd, "server.json");
            if (File.Exists(cwdCandidate))
            {
                return Path.GetFullPath(cwdCandidate);
            }
        }

        string? baseDir = baseDirectory ?? AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            string baseCandidate = Path.Combine(baseDir, "server.json");
            if (File.Exists(baseCandidate))
            {
                return Path.GetFullPath(baseCandidate);
            }
        }

        return null;
    }

    public static string? ResolveConfigPath(
        string[] args,
        Func<string, string?>? getEnv = null,
        string? defaultConfigPath = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        getEnv ??= Environment.GetEnvironmentVariable;

        string? cliConfig = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--config", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for argument '--config'.");
                }

                cliConfig = args[++i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
            {
                cliConfig = arg["--config=".Length..];
            }
        }

        if (cliConfig is not null)
        {
            string trimmed = cliConfig.Trim();
            if (string.IsNullOrEmpty(trimmed) || !File.Exists(trimmed))
            {
                throw new ArgumentException($"Configuration file not found for --config: '{cliConfig}'.", "--config");
            }

            return Path.GetFullPath(trimmed);
        }

        string? envConfig = getEnv("MMA_CONFIG");
        if (!string.IsNullOrWhiteSpace(envConfig))
        {
            string trimmed = envConfig.Trim();
            if (!File.Exists(trimmed))
            {
                throw new ArgumentException($"Configuration file not found for MMA_CONFIG: '{trimmed}'.", "MMA_CONFIG");
            }

            return Path.GetFullPath(trimmed);
        }

        if (!string.IsNullOrWhiteSpace(defaultConfigPath) && File.Exists(defaultConfigPath))
        {
            return Path.GetFullPath(defaultConfigPath);
        }

        return null;
    }

    public static ServerOptions Parse(
        string[] args,
        Func<string, string?>? getEnv = null,
        string? defaultConfigPath = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        getEnv ??= Environment.GetEnvironmentVariable;

        // Tier 1: Built-in defaults.
        IPAddress bindAddress = IPAddress.Any;
        int port = MmaConstants.DefaultPort;
        int maxPlayers = DefaultParsedMaxPlayers;
        string rawMode = MmaConstants.DefaultMode;
        string? stage = null;
        int buffer = DefaultParsedBuffer;

        // Tier 2: Optional JSON configuration file.
        string? activeConfigPath = ResolveConfigPath(args, getEnv, defaultConfigPath);
        if (activeConfigPath is not null)
        {
            ApplyJsonConfig(
                activeConfigPath,
                ref bindAddress,
                ref port,
                ref maxPlayers,
                ref rawMode,
                ref stage,
                ref buffer);
        }

        // Tier 3: Non-whitespace environment variables override JSON config.
        string? envBind = getEnv("MMA_BIND");
        if (!string.IsNullOrWhiteSpace(envBind))
        {
            bindAddress = ParseIpAddress(envBind.Trim(), "MMA_BIND");
        }

        port = ParseInt(getEnv("MMA_PORT"), port, 0, ushort.MaxValue - 1, "MMA_PORT");
        maxPlayers = ParseInt(
            getEnv("MMA_MAX_PLAYERS"),
            maxPlayers,
            MmaConstants.MinPlayers,
            MmaConstants.MaxSupportedPlayers,
            "MMA_MAX_PLAYERS");

        string? envMode = getEnv("MMA_MODE");
        if (!string.IsNullOrWhiteSpace(envMode))
        {
            rawMode = envMode.Trim();
        }

        string? envStage = getEnv("MMA_STAGE");
        if (!string.IsNullOrWhiteSpace(envStage))
        {
            stage = envStage.Trim();
        }

        buffer = ParseInt(getEnv("MMA_BUFFER"), buffer, 0, MmaConstants.MaxOnlineBuffer, "MMA_BUFFER");

        // Tier 4: CLI arguments override JSON config and environment variables.
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string key;
            string? inlineValue = null;

            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                key = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }
            else
            {
                key = arg;
            }

            string ReadValue()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for argument '{key}'.");
                }
                return args[++i];
            }

            switch (key)
            {
                case "--config":
                    _ = ReadValue();
                    break;
                case "--bind":
                    bindAddress = ParseIpAddress(ReadValue().Trim(), "--bind");
                    break;
                case "--port":
                    port = ParseInt(ReadValue(), port, 0, ushort.MaxValue - 1, "--port");
                    break;
                case "--max-players":
                    maxPlayers = ParseInt(ReadValue(), maxPlayers, MmaConstants.MinPlayers, MmaConstants.MaxSupportedPlayers, "--max-players");
                    break;
                case "--mode":
                    rawMode = ReadValue().Trim();
                    if (string.IsNullOrEmpty(rawMode))
                    {
                        throw new ArgumentException("Mode cannot be empty.", "--mode");
                    }
                    break;
                case "--stage":
                    stage = ReadValue().Trim();
                    if (string.IsNullOrEmpty(stage))
                    {
                        throw new ArgumentException("Stage cannot be empty.", "--stage");
                    }
                    break;
                case "--buffer":
                    buffer = ParseInt(ReadValue(), buffer, 0, MmaConstants.MaxOnlineBuffer, "--buffer");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        string canonicalMode = GameModeConfig.Resolve(rawMode).Mode;
        return new ServerOptions(bindAddress, port, maxPlayers, canonicalMode, stage, buffer);
    }

    private static void ApplyJsonConfig(
        string configPath,
        ref IPAddress bindAddress,
        ref int port,
        ref int maxPlayers,
        ref string rawMode,
        ref string? stage,
        ref int buffer)
    {
        JsonDocument document;
        try
        {
            string jsonText = File.ReadAllText(configPath);
            document = JsonDocument.Parse(jsonText, ConfigJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Malformed JSON configuration file '{configPath}': {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"Configuration file '{configPath}' must have a JSON object at the root.");
            }

            int? jsonMaxPlayers = null;
            int? jsonPlayers = null;

            foreach (JsonProperty prop in document.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("Bind") || string.Equals(prop.Name, "Bind", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (prop.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    {
                        throw new ArgumentException($"Invalid 'Bind' value in '{configPath}'. Expected non-empty IPv4 string.");
                    }

                    bindAddress = ParseIpAddress(prop.Value.GetString()!.Trim(), "server.json:Bind");
                }
                else if (string.Equals(prop.Name, "Port", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind != JsonValueKind.Null)
                    {
                        port = ParseJsonInt(prop.Value, 0, ushort.MaxValue - 1, "server.json:Port");
                    }
                }
                else if (string.Equals(prop.Name, "MaxPlayers", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind != JsonValueKind.Null)
                    {
                        jsonMaxPlayers = ParseJsonInt(
                            prop.Value,
                            MmaConstants.MinPlayers,
                            MmaConstants.MaxSupportedPlayers,
                            "server.json:MaxPlayers");
                    }
                }
                else if (string.Equals(prop.Name, "Players", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind != JsonValueKind.Null)
                    {
                        jsonPlayers = ParseJsonInt(
                            prop.Value,
                            MmaConstants.MinPlayers,
                            MmaConstants.MaxSupportedPlayers,
                            "server.json:Players");
                    }
                }
                else if (string.Equals(prop.Name, "Mode", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (prop.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    {
                        throw new ArgumentException($"Invalid 'Mode' value in '{configPath}'. Expected non-empty string.");
                    }

                    rawMode = prop.Value.GetString()!.Trim();
                }
                else if (string.Equals(prop.Name, "Stage", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        stage = null;
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string trimmed = prop.Value.GetString()!.Trim();
                        stage = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                    }
                    else
                    {
                        throw new ArgumentException($"Invalid 'Stage' value in '{configPath}'. Expected string or null.");
                    }
                }
                else if (string.Equals(prop.Name, "Buffer", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind != JsonValueKind.Null)
                    {
                        buffer = ParseJsonInt(prop.Value, 0, MmaConstants.MaxOnlineBuffer, "server.json:Buffer");
                    }
                }
            }

            if (jsonMaxPlayers is not null)
            {
                maxPlayers = jsonMaxPlayers.Value;
            }
            else if (jsonPlayers is not null)
            {
                maxPlayers = jsonPlayers.Value;
            }
        }
    }

    private static int ParseJsonInt(JsonElement element, int min, int max, string source)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int num))
        {
            if (num >= min && num <= max)
            {
                return num;
            }

            throw new ArgumentException($"Invalid value '{num}' for {source}. Expected integer between {min} and {max}.");
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            string? str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) && int.TryParse(str.Trim(), out int parsed) && parsed >= min && parsed <= max)
            {
                return parsed;
            }

            throw new ArgumentException($"Invalid value '{str}' for {source}. Expected integer between {min} and {max}.");
        }

        throw new ArgumentException($"Invalid JSON value kind '{element.ValueKind}' for {source}. Expected integer between {min} and {max}.");
    }

    private static IPAddress ParseIpAddress(string raw, string source)
    {
        if (!IPAddress.TryParse(raw, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid IPv4 address '{raw}' for {source}.");
        }
        return address;
    }

    private static int ParseInt(string? raw, int fallback, int min, int max, string source)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        if (!int.TryParse(raw.Trim(), out int value) || value < min || value > max)
        {
            throw new ArgumentException($"Invalid value '{raw}' for {source}. Expected integer between {min} and {max}.");
        }
        return value;
    }
}
