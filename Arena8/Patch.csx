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

Edit("gml_GlobalScript_drawHeadIcons", code =>
    Replace(code,
        "    if (argument_count == 0)\n    {\n        var charRef = assetRef(charSelected);\n        var imageScale = global.resolutionFactor;",
        "    if (argument_count <= 1)\n    {\n        var charRef = assetRef(charSelected);\n        var imageScale = (argument_count == 1) ? argument[0] : global.resolutionFactor;"));

Edit("gml_GlobalScript_globalSetDefaultOptions", code =>
{
    code = Replace(code, "global.serverPort = 6520;", "global.serverPort = 6528;");
    code = Replace(code, "    global.windowScale = 2;", "    global.windowScale = 3;");
    return Replace(code, @"    window_set_size(global.xResolution * global.windowScale, global.yResolution * global.windowScale);
    global.windowResetPositionX = 150;
    global.windowResetPositionY = 50;
    window_set_position(global.windowResetPositionX, global.windowResetPositionY);", @"    global.arena8WorkArea = [0, 0, display_get_width(), display_get_height() - 48, 16, 48];
    var workAreaFlag = ""-arena8-work-area="";
    for (var flagIndex = 1; flagIndex <= parameter_count(); flagIndex++)
    {
        var flag = parameter_string(flagIndex);
        if (string_pos(workAreaFlag, flag) != 1)
        {
            continue;
        }
        var fields = string_split(string_delete(flag, 1, string_length(workAreaFlag)), "","");
        if (array_length(fields) != 6)
        {
            continue;
        }
        for (var field = 0; field < 6; field++)
        {
            fields[field] = real(""0"" + string_digits(fields[field]));
        }
        if (fields[2] <= fields[4] || fields[3] <= fields[5])
        {
            continue;
        }
        global.arena8WorkArea = fields;
        break;
    }

    global.arena8ResizeWindow = function(arg0, arg1)
    {
        if (window_get_fullscreen())
        {
            exit;
        }
        var area = global.arena8WorkArea;
        var availableWidth = area[2] - area[4];
        var availableHeight = area[3] - area[5];
        var units = max(1, floor(min(availableWidth / 10, availableHeight / 7, (global.xResolution * arg0) / 10)));
        var clientWidth = units * 10;
        var clientHeight = units * 7;
        window_set_size(clientWidth, clientHeight);
        if (arg1)
        {
            window_set_position(area[0] + ((availableWidth - clientWidth) div 2), area[1] + ((availableHeight - clientHeight) div 2));
        }
    };

    global.windowResetPositionX = 150;
    global.windowResetPositionY = 50;
    global.arena8ResizeWindow(global.windowScale, true);");
});

Edit("gml_GlobalScript_mmaLoadData", code =>
{
    code = Replace(code, "            window_set_size(global.xResolution * 2, global.yResolution * 2);", "            global.arena8ResizeWindow(2, true);");
    return Replace(code, "            window_set_size(global.xResolution * global.windowScale, global.yResolution * global.windowScale);", "            global.arena8ResizeWindow(global.windowScale, room == rmInit);");
});

Edit("gml_Object_objMainMenuControl_Step_0", code =>
{
    const string pattern = @"window_set_size\(global\.xResolution \* global\.windowScale, global\.yResolution \* global\.windowScale\);\n\s*window_set_position\(global\.windowResetPositionX, global\.windowResetPositionY\);";
    if (Regex.Matches(code, pattern).Count != 4)
        throw new InvalidOperationException("Unexpected window resize block count in objMainMenuControl_Step_0");
    return Regex.Replace(code, pattern, "global.arena8ResizeWindow(global.windowScale, true);");
});

Edit("gml_Object_objMainMenuControl_Draw_0", code =>
    Replace(code,
        "draw_text(drawX, drawY + (drawMod * 15), \"SCREEN SIZE: \" + string(global.windowScale) + \"X\");",
        "draw_text(drawX, drawY + (drawMod * 15), \"SCREEN SIZE: \" + ((global.windowScale == 3) ? \"FIT\" : (string(global.windowScale) + \"X\")));"));

Edit("gml_Object_objMenuText_Draw_0", code =>
    Replace(code,
        "draw_text(drawX, drawY + 105, string_hash_to_newline(\"SCREEN SIZE: \" + string(global.windowScale) + \"X\"));",
        "draw_text(drawX, drawY + 105, string_hash_to_newline(\"SCREEN SIZE: \" + ((global.windowScale == 3) ? \"FIT\" : (string(global.windowScale) + \"X\"))));"));

foreach (string ctrlName in new[] { "gml_Object_objMatchControlBoss_Create_0", "gml_Object_objVictoryControl_Create_0" })
{
    Edit(ctrlName, code =>
        Replace(code,
            "window_set_size(global.xResolution * global.windowScale, global.yResolution * global.windowScale);",
            "global.arena8ResizeWindow(global.windowScale, false);"));
}

Edit("gml_GlobalScript_initOnlineVariables", code => Slots(code)
    .Replace("global.onlinePingAlarmSet[5] = false;\n    global.onlinePingAlarmSet[6] = false;",
        "global.onlinePingAlarmSet = array_create(9, false);\n    global.arena8NextInputSend = 0;"));
Edit("gml_GlobalScript_cleanOnlineVariables", Slots);

Edit("gml_GlobalScript_hasCurrentInput", code =>
    Replace(code,
        "if (global.onlineFreezeWithoutLockin && arg0 != global.onlinePlayerPort)",
        "if (global.onlineFreezeWithoutLockin && arg0 != global.onlinePlayerPort && (global.onlinePlayerCount <= 4 || global.rollbackEnabled))"));

Edit("gml_GlobalScript_receiveInputsFromClient", code =>
{
    code = Replace(code,
        "        var _keepWindow = (global.onlineBuffer * 2) + 2;",
        @"        var arena8Lockstep = global.onlinePlayerCount > 4 && !global.rollbackEnabled;
        var _keepWindow = arena8Lockstep ? 32 : ((global.onlineBuffer * 2) + 2);
        var aheadWindow = arena8Lockstep ? 48 : 1000;");
    code = Replace(code,
        "if (_received_frame_ID < (global.onlineMatchFrame - _keepWindow) || _received_frame_ID > (global.onlineMatchFrame + 1000))",
        "if (_received_frame_ID < (global.onlineMatchFrame - _keepWindow) || _received_frame_ID > (global.onlineMatchFrame + aheadWindow))");
    return Replace(code,
        "                    _needFrame = false;\n                    if (_current_frame_inputs[1] != _received_inputs)",
        "                    _needFrame = false;\n                    if (arena8Lockstep)\n                    {\n                        break;\n                    }\n                    if (_current_frame_inputs[1] != _received_inputs)");
});

Edit("gml_GlobalScript_removeUnnecessaryInputs", code =>
{
    code = Replace(code,
        "    var keepWindow = (global.onlineBuffer * 2) + 2;",
        "    var keepWindow = (global.onlinePlayerCount > 4 && !global.rollbackEnabled) ? 32 : ((global.onlineBuffer * 2) + 2);");
    return Replace(code,
        "        for (var _n = 0; _n < ds_list_size(global.onlineInputs[_q]); _n++)",
        "        for (var _n = 0; _n < ds_list_size(global.onlineInputConfirm[_q]); _n++)");
});

Edit("gml_GlobalScript_sendInputs", code =>
    Replace(code, "function sendInputs()\n{", @"function sendInputs()
{
    if (argument[0] == ""server"" && global.onlinePlayerCount > 4 && !global.rollbackEnabled && global.matchStarted)
    {
        if (current_time < global.arena8NextInputSend)
        {
            exit;
        }
        global.arena8NextInputSend = current_time + 12;
    }"));

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
    var oldHalign = draw_get_halign();
    var oldValign = draw_get_valign();
    var cellWidth = (sprite_get_width(sprMenuBoxOnlineLobbyPlayers) - 1) div 2;
    var rowOffsets = [24, 65, 121, 162];
    draw_set_font(global.MM3font);
    draw_set_halign(fa_center);
    draw_set_valign(fa_top);
    draw_set_color(c_white);
    draw_text(playerBoxX + (cellWidth div 2), playerBoxY + 9, ""ARENA 8"");
    draw_text(playerBoxX + cellWidth + (cellWidth div 2), playerBoxY + 9, string(global.onlinePlayerCount) + "" / 8"");
    for (var rosterSlot = 1; rosterSlot <= 8; rosterSlot++)
    {
        var connected = rosterSlot <= global.onlinePlayerCount;
        var ready = connected && global.onlineLobbyReady[rosterSlot];
        var localSlot = rosterSlot == global.onlinePlayerPort;
        var leftX = playerBoxX + (((rosterSlot - 1) % 2) * cellWidth);
        var topY = playerBoxY + rowOffsets[(rosterSlot - 1) div 2];
        var centerX = leftX + (cellWidth div 2);
        if (ready)
        {
            draw_set_color(global.mmaGreenDarker);
            draw_rectangle(leftX + 5, topY, (leftX + cellWidth) - 5, topY + 33, false);
        }
        draw_set_color(localSlot ? global.mmaYellow : c_white);
        draw_text(centerX, topY + 5, (localSlot ? "">"" : """") + ""P"" + string(rosterSlot));
        var name = connected ? string_upper(global.onlineNames[rosterSlot]) : ""WAITING"";
        while (string_length(name) > 0 && string_width(name) > (cellWidth - 16))
        {
            name = string_copy(name, 1, string_length(name) - 1);
        }
        draw_set_color(connected ? c_white : global.mmaLightGray);
        draw_text(centerX, topY + 20, name);
    }
    draw_set_halign(oldHalign);
    draw_set_valign(oldValign);
    draw_set_color(oldColor);
    draw_set_font(oldFont);
}");

Edit("gml_Object_objMatchControl_Create_0", code =>
{
    code = Replace(code, "global.freezeAttempted = [false, false, false, false];", "global.freezeAttempted = array_create(8, false);");
    code = Replace(code, "window_set_size(global.xResolution * global.windowScale, global.yResolution * global.windowScale);", "global.arena8ResizeWindow(global.windowScale, false);");
    return Replace(code, "if (debugMinigameMode)\n{\n    global.isMiniGame4P = true;\n}", @"if (debugMinigameMode)
{
    global.isMiniGame4P = true;
}
if (global.online && global.onlinePlayerCount > 4 && !global.rollbackEnabled && !global.matchStarted)
{
    for (var slot = 1; slot <= global.onlinePlayerCount; slot++)
    {
        var frames = global.onlineInputs[slot];
        ds_list_clear(frames);
        for (var frame = 0; frame <= global.onlineBuffer; frame++)
        {
            ds_list_add(frames, [frame, 0]);
        }
    }
}");
});

Edit("gml_Object_objMatchControl_Step_2", code =>
    Replace(code, "global.freezeAttempted = [false, false, false, false];", "global.freezeAttempted = array_create(8, false);"));

Edit("gml_Object_objMatchControl_Other_16", code => @"
if (global.online && global.onlinePlayerCount > 4 && !instance_exists(objSpawnPointP5))
{
    if (room == rmWilyBoss)
    {
        instance_create(64, 200, objSpawnPointP5);
        instance_create(112, 200, objSpawnPointP6);
        instance_create(208, 200, objSpawnPointP7);
        instance_create(256, 200, objSpawnPointP8);
    }
    else if (room == rmWilyMM3Stage2)
    {
        instance_create(32, 192, objSpawnPointP5);
        instance_create(96, 192, objSpawnPointP6);
        instance_create(224, 192, objSpawnPointP7);
        instance_create(288, 192, objSpawnPointP8);
    }
    else
    {
        var originalPoints = [objSpawnPointP1, objSpawnPointP2, objSpawnPointP3, objSpawnPointP4];
        var extraPoints = [objSpawnPointP5, objSpawnPointP6, objSpawnPointP7, objSpawnPointP8];
        var occupiedX = [];
        var occupiedY = [];
        for (var point = 0; point < 4; point++)
        {
            var original = instance_find(originalPoints[point], 0);
            if (!instance_exists(original))
            {
                continue;
            }
            array_push(occupiedX, original.x);
            array_push(occupiedY, original.y);
        }
        var offsets = [-24, 24, -40, 40];
        for (var point = 0; point < 4; point++)
        {
            var original = instance_find(originalPoints[point], 0);
            if (!instance_exists(original) || instance_exists(extraPoints[point]))
            {
                continue;
            }
            var spawnX = original.x;
            var spawnY = original.y;
            var mirrored = (spawnX < ((global.cameraLeftX + global.cameraRightX) / 2)) ? 1 : -1;
            for (var offset = 0; offset < array_length(offsets); offset++)
            {
                var candidate = original.x + (mirrored * offsets[offset]);
                if (candidate < max(12, global.cameraLeftX + 12) || candidate > min(room_width - 12, global.cameraRightX - 12))
                {
                    continue;
                }
                if (collision_rectangle(candidate - 7, spawnY - 28, candidate + 7, spawnY - 7, objSolid, false, false) != noone)
                {
                    continue;
                }
                var occupied = false;
                for (var position = 0; position < array_length(occupiedX); position++)
                {
                    if (abs(candidate - occupiedX[position]) < 16 && abs(spawnY - occupiedY[position]) < 28)
                    {
                        occupied = true;
                    }
                }
                if (occupied)
                {
                    continue;
                }
                spawnX = candidate;
                break;
            }
            instance_create(spawnX, spawnY, extraPoints[point]);
            array_push(occupiedX, spawnX);
            array_push(occupiedY, spawnY);
        }
    }
}
" + code);

Edit("gml_Object_prtSpawnPoint_Other_14", code =>
    Replace(code,
        "    if (outsideView && global.submode != UnknownEnum.Value_13)",
        @"    if (outsideView && global.online && global.onlinePlayerCount > 4 && thisPlayer >= 5 && thisPlayer <= 8)
    {
        var originalPoints = [objSpawnPointP1, objSpawnPointP2, objSpawnPointP3, objSpawnPointP4];
        var anchor = instance_find(originalPoints[thisPlayer - 5], 0);
        if (instance_exists(anchor))
        {
            x = anchor.x;
            y = anchor.y;
            global.spawnX[thisPlayer] = x;
            global.spawnY[thisPlayer] = global.cameraTopY - 63;
            outsideView = false;
        }
    }
    else if (outsideView && global.submode != UnknownEnum.Value_13)"));

Edit("gml_Object_objTeleportPlayer_Create_0", code =>
    Replace(code, "(thisPlayer % 4) == (other.thisPlayer % 4)",
        "(global.online && global.onlinePlayerCount > 4) ? thisPlayer == other.thisPlayer : (thisPlayer % 4) == (other.thisPlayer % 4)"));
Edit("gml_Object_objTeleportPlayer_Other_13", code =>
    Replace(code, "collisionArray[i].thisPlayer == thisPlayer || thisPlayer >= 5",
        "collisionArray[i].thisPlayer == thisPlayer || (thisPlayer >= 5 && !global.online)"));

Edit("gml_Object_objHealthBar_Draw_64", code =>
{
    code = Replace(code, "    var imageScale = global.resolutionFactor;", @"    var arena8Hud = global.online && global.onlinePlayerCount > 4;
    var layoutScale = global.resolutionFactor;
    var imageScale = arena8Hud ? (layoutScale / 2) : layoutScale;");
    code = Replace(code, @"    switch (thisPlayer)
    {
        case 1:
            drawX = 7 - widescreenAdjust;
            textX = 16;
            drawY = 4;
            break;
        case 2:
        case 5:
            drawX = 295 + widescreenAdjust;
            textX = 304;
            drawY = 4;
            break;
        case 3:
        case 6:
            drawX = 7 - widescreenAdjust;
            textX = 16;
            drawY = 116;
            break;
        case 4:
        case 7:
        case 8:
            drawX = 295 + widescreenAdjust;
            textX = 304;
            drawY = 116;
            break;
        default:
            drawX = 7;
            textX = 16;
            drawY = 4;
            break;
    }", @"    var rightSide = (thisPlayer % 2) == 0;
    if (arena8Hud)
    {
        var hudWidth = (18 * imageScale) / layoutScale;
        var inward = (thisPlayer > 4) ? (hudWidth + 6) : 0;
        var margin = (widescreenAdjust > 0) ? 4 : 7;
        drawX = rightSide ? (320 - margin - hudWidth - inward) : (margin + inward);
        drawY = (((thisPlayer - 1) % 4) >= 2) ? (219 - ((90 * imageScale) / layoutScale)) : 5;
        textX = drawX + (hudWidth / 2);
    }
    else
    {
        switch (thisPlayer)
        {
            case 1:
                drawX = 7 - widescreenAdjust;
                textX = 16;
                drawY = 4;
                break;
            case 2:
            case 5:
                drawX = 295 + widescreenAdjust;
                textX = 304;
                drawY = 4;
                break;
            case 3:
            case 6:
                drawX = 7 - widescreenAdjust;
                textX = 16;
                drawY = 116;
                break;
            case 4:
            case 7:
            case 8:
                drawX = 295 + widescreenAdjust;
                textX = 304;
                drawY = 116;
                break;
            default:
                drawX = 7;
                textX = 16;
                drawY = 4;
                break;
        }
    }");
    code = Replace(code, "    drawX *= imageScale;\n    drawY *= imageScale;\n    textX *= imageScale;",
        "    drawX *= layoutScale;\n    drawY *= layoutScale;\n    textX *= layoutScale;");
    code = Replace(code, "    if (global.online || global.replayOnlineMatch)", @"    if (arena8Hud)
    {
        var labelColor = (thisPlayer == global.onlinePlayerPort) ? global.mmaYellow : c_white;
        draw_sprite_ext(sprHealthLabel, thisPlayer - 1, drawX, drawY + (56 * imageScale), imageScale, imageScale, 0, labelColor, 1);
    }
    else if (global.online || global.replayOnlineMatch)");
    code = Replace(code, @"    switch (thisPlayer)
    {
        case 1:
        case 3:
            iconScale = imageScale;
            break;
        case 2:
        case 4:
            iconScale = -imageScale;
            break;
        default:
            iconScale = imageScale;
            break;
    }", @"    if (arena8Hud)
    {
        iconScale = rightSide ? -imageScale : imageScale;
    }
    else
    {
        switch (thisPlayer)
        {
            case 1:
            case 3:
                iconScale = imageScale;
                break;
            case 2:
            case 4:
                iconScale = -imageScale;
                break;
            default:
                iconScale = imageScale;
                break;
        }
    }");
    return Replace(code, "    drawX += (9 * imageScale);\n    drawHeadIcons();\n    localStockCount = global.playerStockCount[thisPlayer];", @"    drawX += (9 * imageScale);
    if (arena8Hud)
    {
        drawHeadIcons(imageScale);
    }
    else
    {
        drawHeadIcons();
    }
    localStockCount = global.playerStockCount[thisPlayer];
    if (arena8Hud)
    {
        var oldFont = draw_get_font();
        var oldColor = draw_get_color();
        var oldHalign = draw_get_halign();
        var oldValign = draw_get_valign();
        draw_set_font(global.MM3font);
        draw_set_color(c_white);
        draw_set_halign(fa_center);
        draw_set_valign(fa_top);
        draw_text_transformed(drawX, drawY + (82 * imageScale), string(localStockCount), imageScale, imageScale, 0);
        draw_set_valign(oldValign);
        draw_set_halign(oldHalign);
        draw_set_color(oldColor);
        draw_set_font(oldFont);
        exit;
    }");
});

group.Import();
ScriptMessage("Created " + gameName + " data.");
