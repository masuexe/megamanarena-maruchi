using System;
using System.Linq;
using System.Text.RegularExpressions;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;

EnsureDataLoaded();

var group = new CodeImportGroup(Data, null, Data.ToolInfo.DecompilerSettings);
var decompiler = new GlobalDecompileContext(Data);

void Edit(string name, Func<string, string> update)
{
    var entry = Data.Code.Single(code => code.Name.Content == name);
    var code = new Underanalyzer.Decompiler.DecompileContext(decompiler, entry, Data.ToolInfo.DecompilerSettings).DecompileToString();
    group.QueueReplace(entry, update(code.Replace("\r\n", "\n")));
}

string Replace(string code, string from, string to)
{
    if (!code.Contains(from))
        throw new InvalidOperationException("Patch point not found: " + from);
    return code.Replace(from, to);
}

Data.GeneralInfo.Name = Data.Strings.MakeString("MegaManArenaDedicated");
Data.GeneralInfo.FileName = Data.Strings.MakeString("MegaManArenaDedicated");

Edit("gml_Object_objFighterInit_Create_0", code =>
{
    // Keep engine initialization but never read or upload a player's profile.
    int a = code.IndexOf("var fileName =");
    int b = code.IndexOf("createActiveCharacterList();", a);
    code = code.Remove(a, b - a);
    a = code.IndexOf("if (global.hasSeenCloudSyncConsent)");
    b = code.IndexOf("instance_create(0, 0, objSaltManager);", a);
    code = code.Remove(a, b - a);
    a = code.IndexOf("var appID =");
    b = code.IndexOf("initOnlineVariables();", a);
    code = code.Remove(a, b - a);
    return Replace(code, "textAlpha = 0;", @"
audio_master_gain(0);
global.hasSeenAccountPrompt = true;
global.newVersion = false;
room_goto(rmMenu);
textAlpha = 0;");
});

Edit("gml_Object_objMainMenuControl_Create_0", code => code + @"
global.online = true;
global.onlineIsHost = true;
global.onlineObserver = false;
global.firstEnteredOnlineLobby = true;
global.isUsingGMPunch = false;
global.observersAllowedByHost = false;
global.onlineLobbySize = 2;
global.mmaDedicatedMode = ""Arena"";
for (var argIndex = 1; argIndex <= parameter_count(); argIndex++)
{
    var flag = parameter_string(argIndex);
    if (string_pos(""-mma-players="", flag) == 1)
        global.onlineLobbySize = clamp(real(string_delete(flag, 1, 13)), 2, 4);
    else if (string_pos(""-mma-mode="", flag) == 1)
        global.mmaDedicatedMode = string_delete(flag, 1, 10);
}
global.mode = 1;
global.submode = 3;
global.subsubmode = 0;
global.onlineMode = 810;
global.isMinigame4P = false;
global.mmaDedicatedStage = ""rmWilyBoss"";
switch (global.mmaDedicatedMode)
{
    case ""Teams"":
        global.submode = 2;
        break;
    case ""Squads"":
        global.onlineMode = 812;
        global.submode = 1;
        global.stockCount = max(global.stockCount, 2);
        break;
    case ""Boss"":
        global.onlineMode = 813;
        global.mode = 3;
        global.submode = 12;
        global.mmaDedicatedStage = ""rmMechaDragon"";
        break;
    case ""Elevator"":
        global.onlineMode = 8002;
        global.mode = 5;
        global.submode = 8;
        global.isMinigame4P = true;
        global.mmaDedicatedStage = ""rmElevator"";
        break;
    case ""Stage"":
        global.onlineMode = 823;
        global.mode = 3;
        global.submode = 11;
        global.isMinigame4P = true;
        global.mmaDedicatedStage = ""rmBombmanStageMode0"";
        break;
}
stageResetDifficulty();
calculateStageDifficultyScore();
global.onlineStageSelection = 839;
global.onlineAutoBuffer = false;
global.onlineBuffer = 6;
global.mmaDedicatedDeadline = 0;
global.mmaDedicatedActivity = array_create(5, 0);
global.onlineServerPort = global.defaultServerPort;
if (!onlineHostServer(global.defaultServerPort, global.defaultServerPort + 1))
{
    show_debug_message(""MMA Dedicated: could not bind UDP port"");
    game_end();
    exit;
}
room_goto(rmOnlineLobby);
");

Edit("gml_Object_objOnlineControl_Create_0", code =>
{
    int pos = code.IndexOf("if (!instance_exists(objGetMyIP))");
    if (pos < 0) throw new InvalidOperationException("IP lookup patch point missing");
    return code.Substring(0, pos) + "alarm[1] = 30;\n";
});

Edit("gml_Object_objOnlineLobby_Create_0", code =>
{
    code = Replace(code, "readOnlineProfileData();", "");
    code = Replace(code, "saveOnlineProfileData();", "");
    code = Replace(code, "saveMatchStatistics();", "");
    code = Replace(code, "instance_create(0, 0, objCloudSyncMode);", "");
    code = Replace(code, "saveDataNow();", "");
    return code + @"
global.onlineObserver = false;
global.startingMatch = false;
global.mmaDedicatedDeadline = 0;
global.onlineLevelChoices = [-4, -4, -4, -4, -4];
for (var dsSlot = 1; dsSlot <= 4; dsSlot++) global.onlineLobbyReady[dsSlot] = false;
with (objOnlineControl) gotoLevelPacketSent = false;
show_debug_message(""MMA Dedicated: lobby ready (UDP "" + string(global.defaultServerPort) + "") "" +
    global.mmaDedicatedMode + "" / "" + string(global.onlineLobbySize) + "" players"");
";
});

Edit("gml_Object_objOnlineLobby_Step_0", _ => "");

Edit("gml_Object_objOnlineControl_Step_0", code => @"
if (global.startingMatch && global.mmaDedicatedDeadline != 0 &&
    current_time > global.mmaDedicatedDeadline)
{
    onlineRemovePlayer(0);
    exit;
}
if (instance_exists(objMatchControl))
{
    for (var dsSlot = 1; dsSlot <= global.onlinePlayerCount; dsSlot++)
    {
        if (current_time - global.mmaDedicatedActivity[dsSlot] > 15000)
        {
            onlineRemovePlayer(0);
            exit;
        }
    }
}
" + Replace(code, "for (var i = 1; i < global.onlinePlayerCount; i++)",
        "for (var i = global.startingMatch ? global.onlinePlayerCount : 0; i < global.onlinePlayerCount; i++)"));

Edit("gml_GlobalScript_onlineRemovePlayer", code =>
    Replace(code, "var disconnectedPlayerPort = arg0;", @"
    var disconnectedPlayerPort = arg0;
    if (disconnectedPlayerPort == 0 || global.startingMatch || instance_exists(objMatchControl))
    {
        for (var dsSlot = 0; dsSlot < ds_list_size(global.clientSocketList); dsSlot++)
            sendDataToClientUDP(ds_list_find_value(global.clientIPList, dsSlot),
                ds_list_find_value(global.clientPortList, dsSlot), [25, 1], [""LOBBY_CLOSED"", 11]);
        ds_list_clear(global.clientIPList);
        ds_list_clear(global.clientSocketList);
        ds_list_clear(global.clientPortList);
        ds_list_clear(global.clientPortListUDP);
        ds_list_clear(global.clientPlayerPortList);
        global.onlinePlayerCount = 0;
        global.onlineWinningPlayer = -4;
        global.firstEnteredOnlineLobby = true;
        global.startingMatch = false;
        global.mmaDedicatedDeadline = 0;
        show_debug_message(""MMA Dedicated: interrupted round; lobby reset"");
        room_goto(rmOnlineLobby);
        return;
    }"));

Edit("gml_Object_objOnlineControl_Other_68", code =>
{
    code = Replace(code, @"
        var connectionAllowed = connectingIP == ""127.0.0.1"" || dataType == UnknownEnum.Value_1 || dataType == UnknownEnum.Value_19 || ds_list_find_index(global.clientIPList, connectingIP) > 0 || ds_list_find_index(global.observerIPList, connectingIP) >= 0;",
@"
        var sourcePort = ds_map_find_value(async_load, ""port"");
        var sourceSlot = 0;
        for (var slot = 0; slot < ds_list_size(global.clientIPList); slot++)
        {
            if (connectingIP == ds_list_find_value(global.clientIPList, slot) &&
                sourcePort == ds_list_find_value(global.clientPortList, slot))
            {
                sourceSlot = slot + 1;
                break;
            }
        }
        var connectionAllowed = dataType == 1 || sourceSlot != 0;");
    int a = code.IndexOf("var duplicateClient = false;");
    int b = code.IndexOf("var packetType = \"DISALLOW_CONNECT\";", a);
    code = code.Remove(a, b - a).Insert(a, "var duplicateClient = sourceSlot != 0;\n                connectingPort = sourcePort;\n                ");
    code = Replace(code, "if ((i + 1) != 1 && (i + 1) != playerPort)", "if ((i + 1) != playerPort)");
    code = Replace(code, "if (global.onlinePlayerCount > 1 && !playerIsObserver)", "if (!playerIsObserver)");
    code = Replace(code, "global.onlinePingStartTimes[global.onlinePlayerCount - 1] = current_time;",
        "global.onlinePingStartTimes[playerPort - 1] = current_time;");
    code = Replace(code, "if (playerPort > 1 || playerIsObserver)", "if (true)");
    code = Replace(code, "mmaDbgMsg(\"Sending online player ID\", newplayerPort);",
        "show_debug_message(\"MMA Dedicated: player joined P\" + string(newplayerPort));");
    code = Replace(code, "var playerIsObserver = buffer_read(readBuffer, buffer_bool);",
        "var playerIsObserver = buffer_read(readBuffer, buffer_bool);\n                    if (playerPort != sourceSlot || playerIsObserver) exit;");
    int serverEnd = code.IndexOf("\nelse if (socketID == global.clientSocket)");
    if (serverEnd < 0) throw new InvalidOperationException("Server branch patch point missing");
    string server = code.Substring(0, serverEnd);
    // For these packet types the first field is the player's assigned slot.
    foreach (int type in new[] { 3, 6, 7, 14, 15, 20, 21, 23, 24, 27, 28, 31 })
    {
        string pattern = @"(case UnknownEnum\.Value_" + type + @":(?:(?!case UnknownEnum\.Value_)[\s\S])*?var playerPort = buffer_read\(readBuffer, buffer_u8\);)";
        if (Regex.IsMatch(server, pattern))
            server = Regex.Replace(server, pattern, "$1\n                if (playerPort != sourceSlot) exit;",  RegexOptions.None);
    }
    server = Replace(server, "switch (dataType)", @"
        if (dataType == 8 || dataType == 12 || dataType == 13 || dataType == 32 || dataType == 34)
            exit;
        switch (dataType)");
    server = Replace(server, "var packetType = \"CHAR CHANGE\";", @"
                global.onlineCharacters[playerPort] = character;
                global.onlineColors[playerPort] = color;
                global.onlineSkins[playerPort] = skin;
                global.onlineCustomColorPrimary[playerPort] = primary;
                global.onlineCustomColorSecondary[playerPort] = secondary;
                global.onlineSlideDownJump[playerPort] = slideSetting;
                var packetType = ""CHAR CHANGE"";");
    server = Replace(server, "var packetType = \"STAGE CHANGE\";", @"
                global.onlineStages[playerPort] = stage;
                var packetType = ""STAGE CHANGE"";");
    server = Replace(server, "var playerPort = receiveSquadList(readBuffer);", @"
                if (global.submode != 1) exit;
                var squadHeader = buffer_tell(readBuffer);
                var entries = buffer_read(readBuffer, buffer_u8);
                if (entries != global.stockCount || buffer_read(readBuffer, buffer_u8) != sourceSlot) exit;
                buffer_seek(readBuffer, buffer_seek_start, squadHeader);
                var playerPort = receiveSquadList(readBuffer);");
    server = Replace(server, "var packetType = \"PLAYER READY\";", @"
                if (!instance_exists(objOnlineLobby) || global.startingMatch) exit;
                if (global.submode == 1 && array_length(global.squadList[playerPort]) != global.stockCount) exit;
                var packetType = ""PLAYER READY"";");
    server = Replace(server, "allReady = allReady == true && global.onlinePlayerCount > 1;",
        "allReady = allReady && global.onlinePlayerCount == global.onlineLobbySize;");
    server = Replace(server, "if (colorTracker[j] != colorTracker[j + 1])",
        "if ((global.submode == 2 && (global.onlineColors[j] div 3) != (global.onlineColors[j + 1] div 3)) ||\n                            (global.submode != 2 && colorTracker[j] != colorTracker[j + 1]))");
    // Before a full lobby locks in, even the last ready player can cancel.
    int unreadyStart = server.IndexOf("var allReady = true;", server.IndexOf("case UnknownEnum.Value_21:"));
    const string unreadyCondition = "if ((!allReady || global.onlinePlayerCount == 1) && global.onlineLobbyReady[playerPort])";
    int unreadyEnd = server.IndexOf(unreadyCondition, unreadyStart);
    server = Replace(server.Remove(unreadyStart, unreadyEnd - unreadyStart), unreadyCondition,
        "if (!global.startingMatch && global.onlineLobbyReady[playerPort])");
    code = server + code.Substring(serverEnd);
    // Keep the map fixed for each mode while clients choose characters.
    int stageStart = code.IndexOf("var stageToGoTo = global.onlineLastStage;");
    int stageEnd = code.IndexOf("mmaDbgMsg(\"stageToGoTo\"", stageStart);
    code = code.Remove(stageStart, stageEnd - stageStart).Insert(stageStart, "var stageToGoTo = global.mmaDedicatedStage;\n                    ");
    // The host no longer receives its own level packet. Run the same match setup as
    // a client, with the server's state and with the spectator countdown offset cleared.
    int startSetup = code.IndexOf("random_set_seed(randomSeed);", code.IndexOf("\nelse if (socketID == global.clientSocket)"));
    int endSetup = code.IndexOf("pingTimer = 0;", startSetup);
    string setup = code.Substring(startSetup, endSetup - startSetup);
    code = Replace(code, "gotoLevelPacketSent = true;", @"
                    gotoLevelPacketSent = true;
                    global.mmaDedicatedDeadline = 0;
                    global.mmaDedicatedActivity = array_create(5, current_time);
                    global.onlineObserver = true;
                    global.onlineMatchFrame = 0;
                    var levelToGoTo = stageToGoTo;
                    if (global.submode == 11)
                    {
                        global.stagemodeSelectedLeg = string_char_at(levelToGoTo, string_length(levelToGoTo));
                        levelToGoTo = string_delete(levelToGoTo, string_length(levelToGoTo), 1);
                    }
                    " + setup + @"
                    with (objOnlineLobbyCountdown) isObserverDelay = false;
                    show_debug_message(""MMA Dedicated: starting "" + global.mmaDedicatedMode +
                        "" / "" + string(global.onlinePlayerCount) + "" players"");");
    code = Replace(code, "case UnknownEnum.Value_15:\n                var playerPort",
        "case UnknownEnum.Value_15:\n                if (!global.startingMatch) exit;\n                var playerPort");
    code = Replace(code, "global.startingMatch = true;", "global.startingMatch = true;\n                        global.mmaDedicatedDeadline = current_time + 10000;");
    code = Replace(code, "var frameID = global.onlineMatchFrame + global.onlineBuffer + 2;",
        @"if (!instance_exists(objMatchControl)) exit;
                var frameID = global.onlineMatchFrame + global.onlineBuffer + 2;
                playerForfeited[playerPort] = true;
                playerForfeitedFrame[playerPort] = frameID;");
    code = Replace(code, "var playerPort = receiveInputsFromClient(readBuffer, \"server\");\n                    if (playerPort != -4 && playerPort <= global.onlinePlayerCount)", @"
                    var header = buffer_tell(readBuffer);
                    buffer_read(readBuffer, buffer_u8);
                    if (buffer_read(readBuffer, buffer_u8) != sourceSlot) exit;
                    buffer_seek(readBuffer, buffer_seek_start, header);
                    global.mmaDedicatedActivity[sourceSlot] = current_time;
                    var playerPort = receiveInputsFromClient(readBuffer, ""server"");
                    if (playerPort != -4 && playerPort <= global.onlinePlayerCount)");
    return code;
});

group.Import();
ScriptMessage("Created Mega Man Arena dedicated server data.");
