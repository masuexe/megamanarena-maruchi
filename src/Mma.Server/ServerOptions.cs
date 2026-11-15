using System.Net;
using Mma.Protocol;

namespace Mma.Server;

/// <summary>
/// Runtime configuration for the headless UDP server, resolved from environment variables and CLI arguments.
/// </summary>
public sealed record ServerOptions(
    IPAddress BindAddress,
    int Port,
    int MaxPlayers,
    string Mode)
{
    public static ServerOptions Parse(string[] args, Func<string, string?>? getEnv = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        getEnv ??= Environment.GetEnvironmentVariable;

        var bindAddress = ParseIpAddress(getEnv("MMA_BIND") ?? "0.0.0.0", "MMA_BIND");
        int port = ParseInt(getEnv("MMA_PORT"), MmaConstants.DefaultPort, 0, 65535, "MMA_PORT");
        int maxPlayers = ParseInt(getEnv("MMA_MAX_PLAYERS"), MmaConstants.DefaultMaxPlayers, MmaConstants.MinPlayers, 64, "MMA_MAX_PLAYERS");
        string mode = string.IsNullOrWhiteSpace(getEnv("MMA_MODE")) ? MmaConstants.DefaultMode : getEnv("MMA_MODE")!.Trim();

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
                case "--bind":
                    bindAddress = ParseIpAddress(ReadValue(), "--bind");
                    break;
                case "--port":
                    port = ParseInt(ReadValue(), port, 0, 65535, "--port");
                    break;
                case "--max-players":
                    maxPlayers = ParseInt(ReadValue(), maxPlayers, MmaConstants.MinPlayers, 64, "--max-players");
                    break;
                case "--mode":
                    mode = ReadValue().Trim();
                    if (string.IsNullOrEmpty(mode))
                    {
                        throw new ArgumentException("Mode cannot be empty.", "--mode");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        return new ServerOptions(bindAddress, port, maxPlayers, mode);
    }

    private static IPAddress ParseIpAddress(string raw, string source)
    {
        if (!IPAddress.TryParse(raw, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
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
        if (!int.TryParse(raw, out int value) || value < min || value > max)
        {
            throw new ArgumentException($"Invalid value '{raw}' for {source}. Expected integer between {min} and {max}.");
        }
        return value;
    }
}
