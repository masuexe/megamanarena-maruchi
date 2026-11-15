using System;
using System.IO;
using System.Linq;
using UndertaleModLib.Decompiler;

EnsureDataLoaded();

string outDir = Environment.GetEnvironmentVariable("MMA_GML_EXPORT_DIR");
if (string.IsNullOrWhiteSpace(outDir))
    throw new InvalidOperationException("MMA_GML_EXPORT_DIR environment variable is required.");

Directory.CreateDirectory(outDir);

string[] keywords = new[]
{
    "Online", "UDP", "Server", "Client", "Lobby", "Squad", "Input",
    "MatchControl", "DirectIP", "MainMenuControl", "FighterInit",
    "globalSetDefaultOptions", "gotoStage", "stageResetDifficulty",
    "calculateStageDifficultyScore", "sendConnectPacket",
    "sendStageDifficulties", "sendDataTo", "receive", "TeleportPlayer",
    "HealthBar"
};

string[] excluded = new[] { "htme_", "htmerpc_", "udphp_", "GMPunch" };

var dc = new GlobalDecompileContext(Data);
int exported = 0;

foreach (var entry in Data.Code)
{
    string name = entry.Name.Content;
    if (name.StartsWith("gml_Script_", StringComparison.Ordinal))
        continue;
    if (excluded.Any(ex => name.Contains(ex, StringComparison.OrdinalIgnoreCase)))
        continue;
    if (!keywords.Any(kw => name.Contains(kw, StringComparison.OrdinalIgnoreCase)))
        continue;

    string gml = new Underanalyzer.Decompiler.DecompileContext(dc, entry, Data.ToolInfo.DecompilerSettings)
        .DecompileToString()
        .Replace("\r\n", "\n");

    File.WriteAllText(Path.Combine(outDir, name + ".gml"), gml);
    exported++;
}

Console.WriteLine($"Exported {exported} GML entries to {outDir}");
