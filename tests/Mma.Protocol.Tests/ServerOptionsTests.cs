using System.Net;
using Mma.Server;

namespace Mma.Protocol.Tests;

public sealed class ServerOptionsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mma-opts-{Guid.NewGuid():N}");

    public ServerOptionsTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string WriteJson(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    [Fact]
    public void Parse_UsesExpectedBuiltInDefaults_AndSkipsMissingDefaultConfig()
    {
        string missingDefault = Path.Combine(_tempRoot, "does-not-exist.json");

        ServerOptions fromNull = ServerOptions.Parse([], _ => null, defaultConfigPath: null);
        ServerOptions fromMissing = ServerOptions.Parse([], _ => null, defaultConfigPath: missingDefault);

        foreach (ServerOptions opts in new[] { fromNull, fromMissing })
        {
            Assert.Equal(IPAddress.Any, opts.BindAddress);
            Assert.Equal(6520, opts.Port);
            Assert.Equal(4, opts.MaxPlayers);
            Assert.Equal("Arena", opts.Mode);
            Assert.Null(opts.Stage);
            Assert.Equal("rmWilyBoss", opts.ResolvedStage);
            Assert.Equal(3, opts.Buffer);
            Assert.False(opts.UsesArena8Protocol);
        }
    }

    [Fact]
    public void Parse_SupportsDedicatedServerPlayersAlias_AndMaxPlayersOverridesPlayers()
    {
        string legacyJsonPath = WriteJson("legacy.json", """{ "Mode": "Teams", "Players": 3 }""");
        ServerOptions legacy = ServerOptions.Parse([], _ => null, defaultConfigPath: legacyJsonPath);
        Assert.Equal("Teams", legacy.Mode);
        Assert.Equal(3, legacy.MaxPlayers);
        Assert.Equal(IPAddress.Any, legacy.BindAddress);
        Assert.Equal(6520, legacy.Port);
        Assert.Equal(3, legacy.Buffer);

        string bothJsonPath = WriteJson("both.json", """{ "MaxPlayers": 6, "Players": 3 }""");
        ServerOptions both = ServerOptions.Parse([], _ => null, defaultConfigPath: bothJsonPath);
        Assert.Equal(6, both.MaxPlayers);
        Assert.True(both.UsesArena8Protocol);
    }

    [Fact]
    public void Parse_SupportsCommentsTrailingCommasNumericStringsUnknownKeys_AndCaseInsensitiveKeys()
    {
        string richJsonPath = WriteJson("rich.json", """
        {
            // Schema and operator notes should be ignored cleanly
            "$schema": "https://example.invalid/server.schema.json",
            "notes": "Hosted Arena8 server",
            /* Block comments and trailing commas are allowed */
            "bind": "127.0.0.1",
            "port": "6528",
            "maxplayers": "8",
            "mode": "arena8",
            "stage": null,
            "buffer": "5",
        }
        """);

        ServerOptions opts = ServerOptions.Parse([], _ => null, defaultConfigPath: richJsonPath);
        Assert.Equal(IPAddress.Loopback, opts.BindAddress);
        Assert.Equal(6528, opts.Port);
        Assert.Equal(8, opts.MaxPlayers);
        Assert.Equal("Arena8", opts.Mode);
        Assert.Null(opts.Stage);
        Assert.Equal("rmWilyBoss", opts.ResolvedStage);
        Assert.Equal(5, opts.Buffer);
        Assert.True(opts.UsesArena8Protocol);

        string emptyStagePath = WriteJson("empty-stage.json", """{ "Stage": "  " }""");
        ServerOptions clearedStage = ServerOptions.Parse([], _ => null, defaultConfigPath: emptyStagePath);
        Assert.Null(clearedStage.Stage);
        Assert.Equal("rmWilyBoss", clearedStage.ResolvedStage);
    }

    [Fact]
    public void Parse_EnforcesFourTierPrecedence_DefaultsThenJsonThenEnvThenCli()
    {
        string defaultJson = WriteJson("default.json", """
        {
            "Bind": "127.0.0.2",
            "Port": 7000,
            "MaxPlayers": 4,
            "Mode": "Boss",
            "Stage": "rmCutman",
            "Buffer": 2
        }
        """);
        string envJson = WriteJson("env.json", """
        {
            "Bind": "127.0.0.3",
            "Port": 7100,
            "MaxPlayers": 6,
            "Mode": "Teams",
            "Stage": "rmGutsman",
            "Buffer": 4
        }
        """);
        string cliJson = WriteJson("cli.json", """
        {
            "Bind": "127.0.0.4",
            "Port": 7200,
            "MaxPlayers": 8,
            "Mode": "Stage",
            "Stage": "rmElecmanStageMode0",
            "Buffer": 5
        }
        """);

        // 1. MMA_CONFIG beats defaultConfigPath; whitespace env vars do NOT override JSON.
        var envOnly = new Dictionary<string, string?>
        {
            ["MMA_CONFIG"] = envJson,
            ["MMA_BIND"] = "   ",
            ["MMA_PORT"] = "7150",
            ["MMA_MODE"] = "Elevator",
            ["MMA_BUFFER"] = ""
        };
        ServerOptions tier2And3 = ServerOptions.Parse([], k => envOnly.GetValueOrDefault(k), defaultJson);
        Assert.Equal(IPAddress.Parse("127.0.0.3"), tier2And3.BindAddress);
        Assert.Equal(7150, tier2And3.Port);
        Assert.Equal(6, tier2And3.MaxPlayers);
        Assert.Equal("Elevator", tier2And3.Mode);
        Assert.Equal("rmGutsman", tier2And3.Stage);
        Assert.Equal(4, tier2And3.Buffer);

        // 2. --config overrides MMA_CONFIG, MMA_* overrides --config JSON, and CLI flags override everything.
        var fullEnv = new Dictionary<string, string?>
        {
            ["MMA_CONFIG"] = envJson,
            ["MMA_BIND"] = "127.0.0.5",
            ["MMA_PORT"] = "7300",
            ["MMA_MAX_PLAYERS"] = "10",
            ["MMA_STAGE"] = "rmQuickmanStageMode0",
            ["MMA_BUFFER"] = "7"
        };
        ServerOptions tier4 = ServerOptions.Parse(
            [$"--config={cliJson}", "--port", "7400", "--mode=Boss", "--buffer=9"],
            k => fullEnv.GetValueOrDefault(k),
            defaultJson);

        Assert.Equal(IPAddress.Parse("127.0.0.5"), tier4.BindAddress); // Env > cliJson
        Assert.Equal(7400, tier4.Port);                               // CLI > Env > cliJson
        Assert.Equal(10, tier4.MaxPlayers);                           // Env > cliJson
        Assert.Equal("Boss", tier4.Mode);                             // CLI > cliJson
        Assert.Equal("rmQuickmanStageMode0", tier4.Stage);            // Env > cliJson
        Assert.Equal(9, tier4.Buffer);                                // CLI > Env > cliJson
    }

    [Fact]
    public void Parse_ThrowsArgumentException_ForMissingExplicitConfigOrInvalidJson()
    {
        string missing = Path.Combine(_tempRoot, "missing.json");

        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", missing]));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([$"--config={missing}"]));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config"]));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([], k => k == "MMA_CONFIG" ? missing : null));

        string malformed = WriteJson("malformed.json", "{ \"Port\": ");
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", malformed]));

        string arrayRoot = WriteJson("array.json", "[1, 2, 3]");
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", arrayRoot]));

        string badPort = WriteJson("badport.json", """{ "Port": 65535 }""");
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", badPort]));

        string badPlayers = WriteJson("badplayers.json", """{ "Players": 1 }""");
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", badPlayers]));

        string badBuffer = WriteJson("badbuffer.json", """{ "Buffer": "not-a-number" }""");
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", badBuffer]));

        string badBind = WriteJson("badbind.json", """{ "Bind": "::1" }""");
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", badBind]));
    }

    [Fact]
    public void ResolveDefaultConfigPath_ChecksCwdFirst_ThenBaseDirectory_ThenNull()
    {
        string cwd = Path.Combine(_tempRoot, "cwd");
        string baseDir = Path.Combine(_tempRoot, "base");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(baseDir);

        Assert.Null(ServerOptions.ResolveDefaultConfigPath(cwd, baseDir));

        string baseConfig = Path.Combine(baseDir, "server.json");
        File.WriteAllText(baseConfig, "{}");
        Assert.Equal(Path.GetFullPath(baseConfig), ServerOptions.ResolveDefaultConfigPath(cwd, baseDir));

        string cwdConfig = Path.Combine(cwd, "server.json");
        File.WriteAllText(cwdConfig, "{}");
        Assert.Equal(Path.GetFullPath(cwdConfig), ServerOptions.ResolveDefaultConfigPath(cwd, baseDir));
    }

    [Fact]
    public void HelpDetectionAndHelpText_CoverAllOptionsEnvVarsJsonAndFirewallPorts()
    {
        Assert.True(ServerOptions.IsHelpRequested(["--help"]));
        Assert.True(ServerOptions.IsHelpRequested(["-h"]));
        Assert.True(ServerOptions.IsHelpRequested(["/?"]));
        Assert.True(ServerOptions.IsHelpRequested(["--port", "6520", "--HELP"]));
        Assert.True(ServerOptions.IsHelpRequested(["-H"]));
        Assert.False(ServerOptions.IsHelpRequested([]));
        Assert.False(ServerOptions.IsHelpRequested(["--port", "6520"]));

        string help = ServerOptions.GetHelpText();
        foreach (string token in new[]
        {
            "--config", "--bind", "--port", "--max-players", "--mode", "--stage", "--buffer", "--help",
            "Bind", "Port", "MaxPlayers", "Players", "Mode", "Stage", "Buffer",
            "MMA_CONFIG", "MMA_BIND", "MMA_PORT", "MMA_MAX_PLAYERS", "MMA_MODE", "MMA_STAGE", "MMA_BUFFER",
            "Port + 1", "6520", "6521", "6528", "6529"
        })
        {
            Assert.Contains(token, help, StringComparison.Ordinal);
        }
    }
}
