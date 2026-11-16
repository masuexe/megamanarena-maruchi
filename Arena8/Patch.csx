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
    Replace(code, "string(global.alphaVersion);", "string(global.alphaVersion) + \"-arena8-deadline\";"));

Edit("gml_GlobalScript_drawHeadIcons", code =>
    Replace(code,
        "    if (argument_count == 0)\n    {\n        var charRef = assetRef(charSelected);\n        var imageScale = global.resolutionFactor;",
        "    if (argument_count <= 1)\n    {\n        var charRef = assetRef(charSelected);\n        var imageScale = (argument_count == 1) ? argument[0] : global.resolutionFactor;"));

Edit("gml_GlobalScript_drawStockCount", code =>
    Replace(code,
        "    var imageScale = global.resolutionFactor;",
        "    var imageScale = (argument_count == 1) ? argument[0] : global.resolutionFactor;"));

Edit("gml_GlobalScript_getOnlineInputs", code =>
    Replace(code, "    var _entry = -4;", @"    var _entry = -4;
    if (global.arena8Enabled)
    {
        if (argument[0] < 1 || argument[0] > global.onlinePlayerCount)
        {
            return false;
        }
        var frame = (global.onlineMatchFrame - global.onlineBuffer) + 1 + _frameOffset;
        var masks = global.arena8Frame(frame);
        if (is_undefined(masks))
        {
            return false;
        }
        readInputsBitmask(masks[argument[0] - 1]);
        return true;
    }"));

Edit("gml_GlobalScript_globalSetDefaultOptions", code =>
{
    code = Replace(code, "global.serverPort = 6520;", "global.serverPort = 6528;");
    code = Replace(code, "    global.windowScale = 2;", "    global.windowScale = 3;");
    code = Replace(code, "    global.maxOnlinePlayerCount = 4;", "    global.maxOnlinePlayerCount = 8;");
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

Edit("gml_Object_objVictoryControl_Create_0", code =>
    Replace(code,
        "window_set_size(global.xResolution * global.windowScale, global.yResolution * global.windowScale);",
        "global.arena8ResizeWindow(global.windowScale, false);"));

Edit("gml_GlobalScript_hasCurrentInput", code =>
    Replace(code,
        "    global.rollbackInputUnconfirmed[arg0] = false;",
        @"    global.rollbackInputUnconfirmed[arg0] = false;
    if (global.arena8Enabled)
    {
        return !is_undefined(global.arena8Frame((global.onlineMatchFrame - global.onlineBuffer) + 1));
    }"));

Edit("gml_GlobalScript_initOnlineVariables", code =>
{
    code = Slots(code)
        .Replace("global.onlinePingAlarmSet[5] = false;\n    global.onlinePingAlarmSet[6] = false;",
            "global.onlinePingAlarmSet = array_create(9, false);");
    return Replace(code, "    show_debug_message(\"initOnlineVariables run!\");", @"    show_debug_message(""initOnlineVariables run!"");
    global.arena8Enabled = false;
    global.arena8Token = 0;
    global.arena8Phase = 0;
    global.arena8HistorySize = 1024;
    global.arena8QueueSize = 128;
    global.arena8FastSpeed = 0;
    global.arena8CleanLobby = false;
    global.arena8Retired = undefined;

    global.arena8RestoreSpeed = function()
    {
        if (global.arena8FastSpeed == 0)
        {
            exit;
        }
        game_set_speed(global.arena8FastSpeed, 0);
        global.arena8FastSpeed = 0;
    };

    global.arena8RetirePhase = function()
    {
        if (!global.arena8Enabled || !global.onlineIsHost)
        {
            exit;
        }
        global.arena8Retired =
        {
            token: global.arena8Token,
            phase: global.arena8Phase,
            count: global.onlinePlayerCount,
            start: global.onlineBuffer + 1,
            last: global.arena8Committed,
            frames: global.arena8Frames,
            expires: current_time + 18000
        };
    };

    global.arena8BeginPhase = function()
    {
        global.arena8RestoreSpeed();
        global.arena8RetirePhase();
        global.arena8Phase++;
        global.arena8Frames = array_create(global.arena8HistorySize, undefined);
        global.arena8Pending = array_create(global.arena8QueueSize, undefined);
        global.arena8Committed = global.onlineBuffer;
        global.arena8Contiguous = global.onlineBuffer;
        global.arena8Neutral = array_create(global.onlinePlayerCount, 0);
        global.arena8Ack = array_create(global.onlinePlayerCount, global.onlineBuffer);
        global.arena8ConfirmLead = max(2, ceil(global.onlineBuffer / 3));
        global.arena8NextUpload = 0;
        global.arena8NewestUpload = global.onlineBuffer;
        global.arena8NextBroadcast = 0;
        global.arena8LastPacket = current_time;
        global.arena8Enabled = true;
        if (global.onlineIsHost)
        {
            global.arena8CleanLobby = true;
        }
    };

    global.arena8Frame = function(arg0)
    {
        if (arg0 <= global.onlineBuffer)
        {
            return global.arena8Neutral;
        }
        var row = global.arena8Frames[arg0 % global.arena8HistorySize];
        if (is_undefined(row) || row.frame != arg0)
        {
            return undefined;
        }
        return row.masks;
    };

    global.arena8Header = function(arg0, arg1, arg2)
    {
        buffer_write(arg0, 3, global.onlineSessionID);
        buffer_write(arg0, 1, 240);
        buffer_write(arg0, 5, arg1);
        buffer_write(arg0, 3, arg2);
    };

    global.arena8ReadHeader = function(arg0, arg1)
    {
        if (!global.arena8Enabled || arg1 < 14)
        {
            return false;
        }
        return buffer_read(arg0, 5) == global.arena8Token && buffer_read(arg0, 3) == global.arena8Phase;
    };

    global.arena8Sample = function(arg0)
    {
        var lead = max(3, ceil(global.onlineBuffer / 2));
        var frame = max(global.onlineMatchFrame, global.arena8Committed + lead);
        if (frame <= global.arena8NewestUpload)
        {
            exit;
        }
        global.arena8NewestUpload = frame;
        global.arena8Pending[frame % global.arena8QueueSize] =
        {
            frame: frame,
            mask: arg0
        };
    };

    global.arena8Upload = function()
    {
        if (global.onlineIsHost || global.onlinePlayerPort < 1 || global.clientSocket < 0 || current_time < global.arena8NextUpload)
        {
            exit;
        }
        global.arena8NextUpload = current_time + 12;
        var packet = buffer_create(160, 0, 1);
        global.arena8Header(packet, global.arena8Token, global.arena8Phase);
        buffer_write(packet, 5, global.arena8Contiguous);
        buffer_write(packet, 1, 0);
        var count = 0;
        var frame = max(global.arena8Committed + 1, global.arena8NewestUpload - 15);
        while (frame <= global.arena8NewestUpload)
        {
            var row = global.arena8Pending[frame % global.arena8QueueSize];
            if (is_undefined(row) || row.frame != frame)
            {
            }
            else
            {
                buffer_write(packet, 5, frame);
                buffer_write(packet, 5, row.mask);
                count++;
            }
            frame++;
        }
        var bytes = buffer_tell(packet);
        buffer_poke(packet, 13, 1, count);
        network_send_udp(global.clientSocket, global.onlineServerIP, global.onlineServerPort, packet, bytes);
        buffer_delete(packet);
    };

    global.arena8ReceiveProposals = function(arg0, arg1, arg2)
    {
        if (arg1 < 14)
        {
            exit;
        }
        var token = buffer_read(arg0, 5);
        var phase = buffer_read(arg0, 3);
        var ack = buffer_read(arg0, 5);
        var count = buffer_read(arg0, 1);
        if (count > 16 || arg1 != (14 + (count * 8)))
        {
            exit;
        }
        if (!global.arena8Enabled || token != global.arena8Token || phase != global.arena8Phase)
        {
            var retired = global.arena8Retired;
            if (is_undefined(retired) || current_time >= retired.expires || retired.token != token || retired.phase != phase || ack > retired.last)
            {
                exit;
            }
            var first = max(retired.start, (retired.last - global.arena8HistorySize) + 1, ack + 1);
            var reply = global.arena8Batch(retired, first, min(first + 19, retired.last));
            network_send_udp(global.serverSocket, ds_list_find_value(global.clientIPList, arg2 - 1), ds_list_find_value(global.clientPortList, arg2 - 1), reply, buffer_tell(reply));
            buffer_delete(reply);
            exit;
        }
        if (global.arena8Departed[arg2] || ack > global.arena8Committed)
        {
            exit;
        }
        global.mmaDedicatedActivity[arg2] = current_time;
        global.arena8Ack[arg2 - 1] = max(global.arena8Ack[arg2 - 1], ack);
        for (var i = 0; i < count; i++)
        {
            var frame = buffer_read(arg0, 5);
            var mask = buffer_read(arg0, 5);
            if (frame <= global.arena8Committed || frame > (global.onlineMatchFrame + 64))
            {
                continue;
            }
            var index = frame % global.arena8QueueSize;
            var row = global.arena8Pending[index];
            if (is_undefined(row) || row.frame != frame)
            {
                row =
                {
                    frame: frame,
                    masks: array_create(global.onlinePlayerCount, -1)
                };
                global.arena8Pending[index] = row;
            }
            if (row.masks[arg2 - 1] == -1)
            {
                row.masks[arg2 - 1] = mask;
            }
        }
    };

    global.arena8Batch = function(arg0, arg1, arg2)
    {
        var count = max(0, (arg2 - arg1) + 1);
        var packet = buffer_create(14 + (count * (4 + (arg0.count * 4))), 0, 1);
        global.arena8Header(packet, arg0.token, arg0.phase);
        buffer_write(packet, 5, arg0.last);
        buffer_write(packet, 1, count);
        for (var frame = arg1; frame <= arg2; frame++)
        {
            buffer_write(packet, 5, frame);
            var row = arg0.frames[frame % global.arena8HistorySize];
            var masks = row.masks;
            for (var slot = 0; slot < arg0.count; slot++)
            {
                buffer_write(packet, 5, masks[slot]);
            }
        }
        return packet;
    };

    global.arena8ServerTick = function()
    {
        var target = (global.onlineMatchFrame - global.onlineBuffer) + 1 + global.arena8ConfirmLead;
        var advanced = target > global.arena8Committed;
        while (global.arena8Committed < target)
        {
            var frame = ++global.arena8Committed;
            var row = global.arena8Pending[frame % global.arena8QueueSize];
            var masks = array_create(global.onlinePlayerCount, 0);
            if (!is_undefined(row) && row.frame == frame)
            {
                for (var slot = 0; slot < global.onlinePlayerCount; slot++)
                {
                    if (row.masks[slot] != -1 && !global.arena8Departed[slot + 1])
                    {
                        masks[slot] = row.masks[slot];
                    }
                }
            }
            global.arena8Frames[frame % global.arena8HistorySize] =
            {
                frame: frame,
                masks: masks
            };
        }
        if (!advanced && current_time < global.arena8NextBroadcast)
        {
            exit;
        }
        global.arena8NextBroadcast = current_time + 200;
        var last = global.arena8Committed;
        var recent = max(global.onlineBuffer + 1, last - 7);
        var oldest = max(global.onlineBuffer + 1, (last - global.arena8HistorySize) + 1);
        var timeline =
        {
            token: global.arena8Token,
            phase: global.arena8Phase,
            count: global.onlinePlayerCount,
            last: last,
            frames: global.arena8Frames
        };
        var packet = global.arena8Batch(timeline, recent, last);
        for (var peer = 0; peer < global.onlinePlayerCount; peer++)
        {
            if (global.arena8Departed[peer + 1])
            {
                continue;
            }
            var ip = ds_list_find_value(global.clientIPList, peer);
            var port = ds_list_find_value(global.clientPortList, peer);
            network_send_udp(global.serverSocket, ip, port, packet, buffer_tell(packet));
            var next = global.arena8Ack[peer] + 1;
            if (next >= oldest && next < recent)
            {
                var repair = global.arena8Batch(timeline, next, min(next + 19, recent - 1));
                network_send_udp(global.serverSocket, ip, port, repair, buffer_tell(repair));
                buffer_delete(repair);
            }
        }
        buffer_delete(packet);
    };

    global.arena8ReceiveFrames = function(arg0, arg1)
    {
        if (!global.arena8ReadHeader(arg0, arg1))
        {
            exit;
        }
        var highest = buffer_read(arg0, 5);
        var count = buffer_read(arg0, 1);
        if (count > 20 || arg1 != (14 + (count * (4 + (global.onlinePlayerCount * 4)))))
        {
            exit;
        }
        var oldest = max(global.onlineBuffer + 1, (highest - global.arena8HistorySize) + 1);
        var needed = max(global.onlineBuffer + 1, (global.onlineMatchFrame - global.onlineBuffer) + 1);
        if (min(global.arena8Contiguous + 1, needed) < oldest)
        {
            show_debug_message(""Arena8: connection fell behind the retained frame history"");
            onlineDisconnect();
            exit;
        }
        global.arena8LastPacket = current_time;
        global.arena8Committed = max(global.arena8Committed, highest);
        for (var i = 0; i < count; i++)
        {
            var frame = buffer_read(arg0, 5);
            var masks = array_create(global.onlinePlayerCount, 0);
            for (var slot = 0; slot < global.onlinePlayerCount; slot++)
            {
                masks[slot] = buffer_read(arg0, 5);
            }
            if (frame >= needed && frame <= highest)
            {
                global.arena8Frames[frame % global.arena8HistorySize] =
                {
                    frame: frame,
                    masks: masks
                };
            }
        }
        while (global.arena8Contiguous < global.arena8Committed && !is_undefined(global.arena8Frame(global.arena8Contiguous + 1)))
        {
            global.arena8Contiguous++;
        }
    };

    global.arena8ClientTick = function()
    {
        if ((current_time - global.arena8LastPacket) > 18000)
        {
            show_debug_message(""Arena8: server input stream timed out"");
            onlineDisconnect();
            exit;
        }
        var queued = global.arena8Contiguous - ((global.onlineMatchFrame - global.onlineBuffer) + 1);
        if (queued > (global.onlineBuffer + 8) && global.matchStarted && !global.onlinePaused && !global.winnerDecided)
        {
            if (global.arena8FastSpeed == 0)
            {
                global.arena8FastSpeed = game_get_speed(0);
                game_set_speed(round(global.arena8FastSpeed * 1.5), 0);
            }
        }
        else
        {
            global.arena8RestoreSpeed();
        }
    };");
});

Edit("gml_GlobalScript_cleanOnlineVariables", code =>
{
    code = Slots(code);
    return Replace(code, "    initOnlineVariables();", @"    if (variable_global_exists(""arena8RestoreSpeed""))
    {
        global.arena8RestoreSpeed();
    }
    initOnlineVariables();");
});

Edit("gml_GlobalScript_removeUnnecessaryInputs", code =>
    Replace(code,
        "        for (var _n = 0; _n < ds_list_size(global.onlineInputs[_q]); _n++)",
        "        for (var _n = 0; _n < ds_list_size(global.onlineInputConfirm[_q]); _n++)"));

Edit("gml_GlobalScript_mapControls", code =>
{
    const string target = @"            if (!global.onlineObserver)
            {
                var alreadyHasInput = false;
                var list = global.onlineInputs[global.onlinePlayerPort];
                var listSize = ds_list_size(list);
                var i = listSize - 1;
                while (i >= 0)
                {
                    var listEntry = ds_list_find_value(list, i);
                    if (listEntry[0] == global.onlineMatchFrame)
                    {
                        alreadyHasInput = true;
                        break;
                    }
                    i--;
                }
                if (alreadyHasInput == false)
                {
                    ds_list_add(global.onlineInputs[global.onlinePlayerPort], getInputsArray());
                }";
    return Replace(code, target, @"            if (!global.onlineObserver)
            {
                if (global.arena8Enabled)
                {
                    global.arena8Sample(getInputsBitmask());
                }
                else
                {
                    var alreadyHasInput = false;
                    var list = global.onlineInputs[global.onlinePlayerPort];
                    var listSize = ds_list_size(list);
                    var i = listSize - 1;
                    while (i >= 0)
                    {
                        var listEntry = ds_list_find_value(list, i);
                        if (listEntry[0] == global.onlineMatchFrame)
                        {
                            alreadyHasInput = true;
                            break;
                        }
                        i--;
                    }
                    if (alreadyHasInput == false)
                    {
                        ds_list_add(global.onlineInputs[global.onlinePlayerPort], getInputsArray());
                    }
                }");
});

Edit("gml_GlobalScript_sendInputs", code =>
    Replace(code, "function sendInputs()\n{", @"function sendInputs()
{
    if (global.arena8Enabled)
    {
        if (argument[0] == ""server"")
        {
            global.arena8Upload();
        }
        exit;
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
Edit("gml_Object_objBossControlWilyMachine1_Other_13", code =>
    Replace(code,
        "                if (phaseTimer == 480)\n                {\n                    for (var i = 1; i <= 4; i++)",
        "                if (phaseTimer == 480)\n                {\n                    for (var i = 1; i <= (global.online ? global.onlinePlayerCount : 4); i++)"));

Edit("gml_Object_objOnlineControl_Create_0", code => code
    .Replace("i <= 6", "i <= 8")
    .Replace("if (global.onlinePingAlarmSet[i] == true)", "if (i <= 4 && global.onlinePingAlarmSet[i])"));

Edit("gml_Object_objOnlineControl_Draw_73", code => @"if (global.arena8Enabled)
{
    global.arena8Upload();
}
" + code);

Edit("gml_Object_objOnlineControl_Step_0", code =>
{
    const string startMarker = "global.isPlayerIn[1] = true;";
    int start = code.IndexOf(startMarker);
    int end = code.IndexOf("\n}\nfor (var p", start);
    if (start < 0 || end < 0) throw new InvalidOperationException("Active player block missing");
    code = code.Remove(start, end - start).Insert(start, @"
    for (var activeSlot = 1; activeSlot <= 8; activeSlot++)
        global.isPlayerIn[activeSlot] = activeSlot <= global.onlinePlayerCount;");
    if (dedicated)
    {
        code = code.Replace("if (instance_exists(objMatchControl))", "if (instance_exists(objMatchControl) || instance_exists(objMatchControlBoss))");
        return Replace(code, "\nenum UnknownEnum", @"
if (global.arena8Enabled)
{
    global.arena8ServerTick();
}

enum UnknownEnum");
    }
    return Replace(code, "\nenum UnknownEnum", @"
if (global.arena8Enabled)
{
    global.arena8ClientTick();
}

enum UnknownEnum");
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
                            global.teamNumber[matchSlot] = (global.mode == 3 || global.submode == 8) ? 1 : matchSlot;
                        }
                    }
                    ");
    code = code.Replace("alarm[playerPort] = global.onlinePingInterval;",
        "if (playerPort >= 2 && playerPort <= 4) alarm[playerPort] = global.onlinePingInterval;");
    code = code.Replace("instance_exists(objMatchControl)", "(instance_exists(objMatchControl) || instance_exists(objMatchControlBoss))");
    code = Replace(code, @"                        case 810:
                            global.mode = UnknownEnum.Value_1;
                            global.submode = UnknownEnum.Value_3;
                            global.isMinigame4P = false;
                            break;", @"                        case 810:
                            global.mode = UnknownEnum.Value_1;
                            global.submode = UnknownEnum.Value_3;
                            global.isMinigame4P = false;
                            break;
                        case 811:
                            global.mode = 1;
                            global.submode = 2;
                            global.isMinigame4P = false;
                            break;");
    code = Replace(code,
        "                    global.onlineServerIP = global.serverIP;\n                    global.onlineServerPort = global.defaultServerPort;",
        "                    global.onlineServerIP = global.serverIP;\n                    global.arena8ServerIP = onlineIPconvert(ds_map_find_value(async_load, \"ip\"));\n                    global.arena8ServerPort = ds_map_find_value(async_load, \"port\");\n                    global.onlineServerPort = global.defaultServerPort;");

    int clientIdx = code.IndexOf("\nelse if (socketID == global.clientSocket)");
    if (clientIdx < 0) throw new InvalidOperationException("Client branch missing");
    string serverPart = code.Substring(0, clientIdx);
    string clientPart = code.Substring(clientIdx);

    if (dedicated)
    {
        serverPart = Replace(serverPart, "array_create(5, current_time)", "array_create(9, current_time)");
        serverPart = Replace(serverPart, "var randomSeed = random_get_seed();",
            "var randomSeed = random_get_seed();\n                    global.arena8Token = randomSeed % 4294967296;\n                    global.arena8Phase = 0;\n                    global.arena8Departed = array_create(9, false);");
        serverPart = Replace(serverPart,
            "        if (dataType == 8 || dataType == 12 || dataType == 13 || dataType == 32 || dataType == 34)",
            @"        if (dataType == 240)
        {
            if (sourceSlot != 0)
                global.arena8ReceiveProposals(readBuffer, ds_map_find_value(async_load, ""size""), sourceSlot);
            exit;
        }
        if (dataType == 8 || dataType == 12 || dataType == 13 || dataType == 32 || dataType == 34)");
    }

    clientPart = Replace(clientPart, "        switch (dataType)\n        {\n            case UnknownEnum.Value_4:", @"        if (dataType == 240)
        {
            if (global.arena8Enabled && global.arena8ServerIP == onlineIPconvert(ds_map_find_value(async_load, ""ip"")) && global.arena8ServerPort == ds_map_find_value(async_load, ""port""))
            {
                global.arena8ReceiveFrames(readBuffer, ds_map_find_value(async_load, ""size""));
            }
            exit;
        }
        switch (dataType)
        {
            case UnknownEnum.Value_4:");
    clientPart = Replace(clientPart, "                    random_set_seed(randomSeed);",
        "                    global.arena8Token = randomSeed % 4294967296;\n                    global.arena8Phase = 0;\n                    global.arena8Departed = array_create(9, false);\n                    random_set_seed(randomSeed);");

    return serverPart + clientPart;
});

Edit("gml_Object_objMainMenuControl_Create_0", code =>
{
    if (!dedicated) return code;
    code = Replace(code, "global.onlineLobbySize = 2;", "global.onlineLobbySize = 8;");
    code = Replace(code, "clamp(real(string_delete(flag, 1, 13)), 2, 4)", "clamp(real(string_delete(flag, 1, 13)), 2, 8)");
    code = Replace(code, "global.mmaDedicatedMode = \"Arena\";", "global.mmaDedicatedMode = \"Arena8\";");
    return code.Replace("array_create(5, 0)", "array_create(9, 0)");
});

Edit("gml_Object_objOnlineLobby_Create_0", code =>
{
    code = Slots(code)
        .Replace("imgIndex = [0, 0, 0, 0];", "imgIndex = array_create(9, 0);")
        .Replace("saveMatchStatistics();", "")
        .Replace("instance_create(0, 0, objCloudSyncMode);", "");
    code = Replace(code, "global.startingMatch = false;", @"global.startingMatch = false;
if (global.onlineMode == 813 || global.onlineMode == 823)
{
    global.subsubmode = 0;
}");
    return Replace(code, "event_perform(ev_destroy, 0);", @"event_perform(ev_destroy, 0);
global.arena8RetirePhase();
global.arena8Enabled = false;
global.arena8RestoreSpeed();");
});

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
    var modeLabel = ""ARENA 8"";
    switch (global.submode)
    {
        case 1:
            modeLabel = ""SQUADS"";
            break;
        case 2:
            modeLabel = ""TEAMS"";
            break;
        case 8:
            modeLabel = ""ELEVATOR"";
            break;
        case 11:
            modeLabel = ""STAGE"";
            break;
        case 12:
            modeLabel = ""BOSS"";
            break;
    }
    draw_text(playerBoxX + (cellWidth div 2), playerBoxY + 9, modeLabel);
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
if (global.online && global.onlinePlayerCount > 4 && !global.rollbackEnabled)
{
    global.arena8BeginPhase();
}");
});

Edit("gml_Object_objMatchControl_Step_2", code =>
    Replace(code, "global.freezeAttempted = [false, false, false, false];", "global.freezeAttempted = array_create(8, false);"));

Edit("gml_Object_objMatchControl_Other_16", code => @"
if (global.online && global.onlinePlayerCount > 4 && (global.mode == 3 || global.submode == 8))
{
    var originalPoints = [objSpawnPointP1, objSpawnPointP2, objSpawnPointP3, objSpawnPointP4];
    var extraPoints = [objSpawnPointP5, objSpawnPointP6, objSpawnPointP7, objSpawnPointP8];
    for (var slot = 0; slot < (global.onlinePlayerCount - 4); slot++)
    {
        if (instance_exists(extraPoints[slot]))
        {
            continue;
        }
        var anchor = instance_find(originalPoints[slot], 0);
        if (!instance_exists(anchor))
        {
            anchor = instance_find(objSpawnPointP1, 0);
        }
        if (!instance_exists(anchor))
        {
            anchor = instance_find(objSpawnPointP1moving, 0);
        }
        if (instance_exists(anchor))
        {
            instance_create(anchor.x, anchor.y, extraPoints[slot]);
        }
    }
}
if (global.online && global.onlinePlayerCount > 4 && global.mode == 1 && !instance_exists(objSpawnPointP5))
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

Edit("gml_Object_objMatchControlBoss_Create_0", code =>
{
    code = Replace(code, "window_set_size(global.xResolution * global.windowScale, global.yResolution * global.windowScale);", "global.arena8ResizeWindow(global.windowScale, false);");
    code = Replace(code, "if (global.online && i <= 4)", "if (global.online && i <= global.maxOnlinePlayerCount)");
    code = Replace(code, "global.freezeAttempted = [false, false, false, false];", "global.freezeAttempted = array_create(8, false);");
    code = Replace(code, "for (var i = 1; i <= 4; i++)\n{\n    global.onlinePlayerForfeited[i] = false;\n}", "for (var i = 1; i <= 8; i++)\n{\n    global.onlinePlayerForfeited[i] = false;\n}");
    return Replace(code, "alarmTimer[6] = 5;", @"alarmTimer[6] = 5;
if (global.online && global.onlinePlayerCount > 4 && !global.rollbackEnabled)
{
    global.arena8BeginPhase();
}");
});

Edit("gml_Object_objMatchControlBoss_Step_2", code =>
    Replace(code, "global.freezeAttempted = [false, false, false, false];", "global.freezeAttempted = array_create(8, false);"));

Edit("gml_Object_objMatchControlBoss_Other_16", code => @"
if (global.online && global.onlinePlayerCount > 4 && (global.mode == 3 || global.submode == 8))
{
    var originalPoints = [objSpawnPointP1, objSpawnPointP2, objSpawnPointP3, objSpawnPointP4];
    var extraPoints = [objSpawnPointP5, objSpawnPointP6, objSpawnPointP7, objSpawnPointP8];
    for (var slot = 0; slot < (global.onlinePlayerCount - 4); slot++)
    {
        if (instance_exists(extraPoints[slot]))
        {
            continue;
        }
        var anchor = instance_find(originalPoints[slot], 0);
        if (!instance_exists(anchor))
        {
            anchor = instance_find(objSpawnPointP1, 0);
        }
        if (!instance_exists(anchor))
        {
            anchor = instance_find(objSpawnPointP1moving, 0);
        }
        if (instance_exists(anchor))
        {
            instance_create(anchor.x, anchor.y, extraPoints[slot]);
        }
    }
}
" + code);

Edit("gml_Object_prtSpawnPoint_Other_14", code =>
    Replace(code,
        "    if (outsideView && global.submode != UnknownEnum.Value_13)",
        @"    if (global.online && global.onlinePlayerCount > 4 && (outsideView || global.submode == 11 || global.submode == 8) && thisPlayer >= 5 && thisPlayer <= 8)
    {
        var originalPoints = [objSpawnPointP1, objSpawnPointP2, objSpawnPointP3, objSpawnPointP4];
        var anchor = instance_find(originalPoints[thisPlayer - 5], 0);
        if (!instance_exists(anchor))
        {
            anchor = instance_find(objSpawnPointP1, 0);
        }
        if (!instance_exists(anchor))
        {
            anchor = instance_find(objSpawnPointP1moving, 0);
        }
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
    code = Replace(code, "    var imageScale = global.resolutionFactor;", @"    var arena8Hud = global.online && global.onlinePlayerCount > 4 && thisPlayer <= global.onlinePlayerCount;
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
        if (global.submode == 2 && variable_instance_exists(id, ""primaryColorLabel""))
        {
            labelColor = primaryColorLabel;
        }
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
        if (global.mode == 3 || global.submode == 8)
        {
            drawStockCount(imageScale);
        }
        else
        {
            draw_set_halign(fa_center);
            draw_set_valign(fa_top);
            draw_text_transformed(drawX, drawY + (82 * imageScale), string(localStockCount), imageScale, imageScale, 0);
        }
        draw_set_valign(oldValign);
        draw_set_halign(oldHalign);
        draw_set_color(oldColor);
        draw_set_font(oldFont);
        exit;
    }");
});

group.Import();
ScriptMessage("Created " + gameName + " data.");
