using System;
using System.Linq;
using System.Text.RegularExpressions;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;

EnsureDataLoaded();

string inputName = Data.GeneralInfo.Name.Content;
bool dedicated = inputName == "MegaManArenaDedicated";
if (!dedicated && inputName != "MegaManArena")
    throw new InvalidOperationException("Expected the 5.0 client or the four-player dedicated build.");

string gameName = dedicated ? "MegaManArena8Server" : "MegaManArena8Client";
Data.GeneralInfo.Name = Data.Strings.MakeString(gameName);
Data.GeneralInfo.FileName = Data.Strings.MakeString(gameName);
var group = new CodeImportGroup(Data, null, Data.ToolInfo.DecompilerSettings);
var dc = new GlobalDecompileContext(Data);

string Replace(string code, string from, string to)
{
    if (!code.Contains(from))
        throw new InvalidOperationException("Patch point missing: " + from);
    return code.Replace(from, to);
}

void Edit(string name, Func<string, string> update)
{
    var entry = Data.Code.Single(x => x.Name.Content == name);
    var code = new Underanalyzer.Decompiler.DecompileContext(dc, entry, Data.ToolInfo.DecompilerSettings)
        .DecompileToString().Replace("\r\n", "\n");
    group.QueueReplace(entry, update(code));
}

string Slots(string code) => code
    .Replace(" <= 4;", " <= 8;")
    .Replace("playerPort > 4", "playerPort > 8")
    .Replace("[-4, -4, -4, -4, -4]", "array_create(9, -4)")
    .Replace("[current_time, current_time, current_time, current_time]", "array_create(8, current_time)")
    .Replace("[[], [], [], []]", "[[], [], [], [], [], [], [], []]");

Edit("gml_GlobalScript_getVersionString", code =>
    Replace(code, "string(global.alphaVersion);", "string(global.alphaVersion) + \"-arena8\";"));
Edit("gml_GlobalScript_globalSetDefaultOptions", code =>
    Replace(code, "global.serverPort = 6520;", "global.serverPort = 6528;"));
Edit("gml_GlobalScript_initOnlineVariables", code => Slots(code)
    .Replace("global.onlinePingAlarmSet[5] = false;\n    global.onlinePingAlarmSet[6] = false;",
        "global.onlinePingAlarmSet = array_create(9, false);"));
Edit("gml_GlobalScript_cleanOnlineVariables", Slots);
if (dedicated)
{
    Edit("gml_GlobalScript_sendDataToNewPlayer", _ => @"
function sendDataToNewPlayer(arg0, arg1, arg2, arg3)
{
    var message = buffer_create(256, buffer_grow, 1);
    buffer_write(message, buffer_u16, global.onlineSessionID);
    buffer_write(message, buffer_u8, 4);
    buffer_write(message, buffer_u16, global.onlineSessionID);
    buffer_write(message, buffer_u8, arg2);
    buffer_write(message, buffer_u8, arg3);
    for (var rosterSlot = 1; rosterSlot < arg2; rosterSlot++)
    {
        buffer_write(message, buffer_string, global.onlineNames[rosterSlot]);
        buffer_write(message, buffer_s16, global.onlineCharacters[rosterSlot]);
        buffer_write(message, buffer_u8, global.onlineColors[rosterSlot]);
        buffer_write(message, buffer_u32, global.onlineCustomColorPrimary[rosterSlot]);
        buffer_write(message, buffer_u32, global.onlineCustomColorSecondary[rosterSlot]);
        buffer_write(message, buffer_u8, global.onlineSkins[rosterSlot]);
        buffer_write(message, buffer_string, global.onlineStages[rosterSlot]);
        buffer_write(message, buffer_bool, global.onlineLobbyReady[rosterSlot]);
        buffer_write(message, buffer_u8, global.onlineSlideDownJump[rosterSlot]);
        buffer_write(message, buffer_bool, global.onlineCheatEnabled[rosterSlot]);
    }
    network_send_udp(global.serverSocket, arg0, arg1, message, buffer_tell(message));
    buffer_delete(message);
}");
}
Edit("gml_Object_objOnlineLobbyCountdown_Step_0", code => Slots(code)
    .Replace("gotoStage(global.stageSelected);",
        "if (global.online && global.onlinePlayerCount > 4) global.isReplayEnabled = false;\n            gotoStage(global.stageSelected);"));
Edit("gml_Object_objMatchControl_Other_17", Slots);
Edit("gml_Object_objOnlineControl_Create_0", code => code
    .Replace("i <= 6", "i <= 8")
    .Replace("if (global.onlinePingAlarmSet[i] == true)", "if (i <= 4 && global.onlinePingAlarmSet[i])"));

Edit("gml_Object_objOnlineControl_Step_0", code =>
{
    const string startMarker = "global.isPlayerIn[1] = true;";
    int start = code.IndexOf(startMarker);
    int end = code.IndexOf("\n}\nfor (var p", start);
    if (start < 0 || end < 0) throw new InvalidOperationException("Active player block missing");
    return code.Remove(start, end - start).Insert(start, @"
    for (var activeSlot = 1; activeSlot <= 8; activeSlot++)
        global.isPlayerIn[activeSlot] = activeSlot <= global.onlinePlayerCount;");
});

Edit("gml_Object_objOnlineControl_Other_68", code =>
{
    code = Slots(code);
    const string pattern = @"global\.isSelected\[1\] = true;[\s\S]*?(?=for \(var k = 1; k <= 8; k\+\+\))";
    int expected = dedicated ? 2 : 1;
    if (Regex.Matches(code, pattern).Count != expected)
        throw new InvalidOperationException("Incorrect number of match setup blocks");
    code = Regex.Replace(code, pattern, @"
                    for (var matchSlot = 1; matchSlot <= 8; matchSlot++)
                    {
                        var present = matchSlot <= global.onlinePlayerCount;
                        global.isSelected[matchSlot] = present;
                        global.isPlayerIn[matchSlot] = present;
                        global.isCPU[matchSlot] = false;
                        if (present)
                        {
                            global.characterSelected[matchSlot] = global.onlineCharacters[matchSlot];
                            global.charColor[matchSlot] = global.onlineColors[matchSlot];
                            global.charSkin[matchSlot] = global.onlineSkins[matchSlot];
                            global.slideDownJump[matchSlot] = global.onlineSlideDownJump[matchSlot];
                            global.teamNumber[matchSlot] = matchSlot;
                        }
                    }
                    ");
    code = code.Replace("alarm[playerPort] = global.onlinePingInterval;",
        "if (playerPort >= 2 && playerPort <= 4) alarm[playerPort] = global.onlinePingInterval;");
    if (dedicated)
        code = Replace(code, "array_create(5, current_time)", "array_create(9, current_time)");
    return code;
});

Edit("gml_Object_objMainMenuControl_Create_0", code =>
{
    if (!dedicated) return code;
    int start = code.IndexOf("global.onlineLobbySize = 2;");
    int end = code.IndexOf("stageResetDifficulty();", start);
    if (start < 0 || end < 0) throw new InvalidOperationException("Dedicated mode configuration missing");
    code = code.Remove(start, end - start).Insert(start, @"
global.onlineLobbySize = 8;
global.mmaDedicatedMode = ""Arena8"";
global.mode = 1;
global.submode = 3;
global.subsubmode = 0;
global.onlineMode = 810;
global.isMinigame4P = false;
global.mmaDedicatedStage = ""rmWilyBoss"";
");
    return code.Replace("array_create(5, 0)", "array_create(9, 0)");
});

Edit("gml_Object_objOnlineLobby_Create_0", code => Slots(code)
    .Replace("imgIndex = [0, 0, 0, 0];", "imgIndex = array_create(9, 0);")
    .Replace("saveMatchStatistics();", "")
    .Replace("instance_create(0, 0, objCloudSyncMode);", ""));

Edit("gml_GlobalScript_onlineLobbyDrawPlayers", _ => @"
function onlineLobbyDrawPlayers()
{
    var oldFont = draw_get_font();
    var oldColor = draw_get_color();
    draw_set_font(-1);
    draw_set_color(c_aqua);
    draw_text(playerBoxX + 8, playerBoxY + 8, ""ARENA 8  "" + string(global.onlinePlayerCount) + "" / 8"");
    for (var rosterSlot = 1; rosterSlot <= 8; rosterSlot++)
    {
        var connected = rosterSlot <= global.onlinePlayerCount;
        var label = connected ? string_copy(global.onlineNames[rosterSlot], 1, 8) : ""WAITING"";
        draw_set_color(connected && global.onlineLobbyReady[rosterSlot] ? c_lime : (connected ? c_white : c_gray));
        var marker = rosterSlot == global.onlinePlayerPort ? "">"" : "" "";
        draw_text(playerBoxX + 8, playerBoxY + 24 + (rosterSlot - 1) * 18,
            marker + ""P"" + string(rosterSlot) + "" "" + label);
    }
    draw_set_color(oldColor);
    draw_set_font(oldFont);
}");

foreach (string matchEvent in new[] { "Create_0", "Step_2" })
{
    Edit("gml_Object_objMatchControl_" + matchEvent, code =>
        Replace(code, "global.freezeAttempted = [false, false, false, false];",
            "global.freezeAttempted = array_create(8, false);"));
}
Edit("gml_Object_objMatchControl_Other_16", code => @"
if (global.online && room == rmWilyBoss && global.onlinePlayerCount > 4 &&
    !instance_exists(objSpawnPointP5))
{
    instance_create(64, 200, objSpawnPointP5);
    instance_create(112, 200, objSpawnPointP6);
    instance_create(208, 200, objSpawnPointP7);
    instance_create(256, 200, objSpawnPointP8);
}
" + code);
Edit("gml_Object_objTeleportPlayer_Create_0", code =>
    Replace(code, "(thisPlayer % 4) == (other.thisPlayer % 4)",
        "(global.online && global.onlinePlayerCount > 4) ? thisPlayer == other.thisPlayer : (thisPlayer % 4) == (other.thisPlayer % 4)"));
Edit("gml_Object_objTeleportPlayer_Other_13", code =>
    Replace(code, "collisionArray[i].thisPlayer == thisPlayer || thisPlayer >= 5",
        "collisionArray[i].thisPlayer == thisPlayer || (thisPlayer >= 5 && !global.online)"));
Edit("gml_Object_objHealthBar_Draw_64", code => @"
if (global.online && global.onlinePlayerCount > 4)
{
    var oldFont = draw_get_font();
    var oldColor = draw_get_color();
    var scale = global.resolutionFactor;
    var rightSide = thisPlayer > 4;
    var hudX = (rightSide ? 268 : 4) * scale;
    var hudY = (5 + ((thisPlayer - 1) mod 4) * 24) * scale;
    var hp = clamp(global.playerHealth[thisPlayer] / global.healthMax, 0, 1);
    var ep = clamp(global.playerAmmo[thisPlayer] / global.maxEP, 0, 1);
    draw_set_font(-1);
    draw_set_color(c_black);
    draw_rectangle(hudX, hudY, hudX + 47 * scale, hudY + 20 * scale, false);
    draw_set_color(thisPlayer == global.onlinePlayerPort ? c_aqua : c_white);
    draw_text(hudX + scale, hudY, ""P"" + string(thisPlayer) + "" x"" + string(global.playerStockCount[thisPlayer]));
    draw_set_color(c_lime);
    draw_rectangle(hudX + scale, hudY + 13 * scale, hudX + (1 + 44 * hp) * scale, hudY + 15 * scale, false);
    draw_set_color(c_yellow);
    draw_rectangle(hudX + scale, hudY + 17 * scale, hudX + (1 + 44 * ep) * scale, hudY + 19 * scale, false);
    draw_set_color(oldColor);
    draw_set_font(oldFont);
    exit;
}
" + code);

group.Import();
ScriptMessage("Created " + gameName + " data.");
