// Testing only. Drive the Direct IP popup, then send and check real game inputs.
using System;
using System.Linq;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

EnsureDataLoaded();
var group = new CodeImportGroup(Data, null, Data.ToolInfo.DecompilerSettings);
var dc = new GlobalDecompileContext(Data);
Data.GeneralInfo.Name = Data.Strings.MakeString("MegaManArena8QA");
Data.GeneralInfo.FileName = Data.Strings.MakeString("MegaManArena8QA");

void Edit(string name, Func<string, string> edit)
{
    var entry = Data.Code.Single(x => x.Name.Content == name);
    var code = new Underanalyzer.Decompiler.DecompileContext(dc, entry, Data.ToolInfo.DecompilerSettings)
        .DecompileToString().Replace("\r\n", "\n");
    group.QueueReplace(entry, edit(code));
}

Edit("gml_Object_objFighterInit_Create_0", code => code + @"
audio_master_gain(0);
room_goto(rmMenu);
");
Edit("gml_Object_objMainMenuControl_Create_0", code => code + @"
global.serverIP = ""127.0.0.1"";
global.isUsingGMPunch = false;
global.onlineObserver = false;
global.qaRound = 0;
global.qaCaptured = false;
global.qaFinishRound = false;
for (var qaArg = 1; qaArg <= parameter_count(); qaArg++)
    if (parameter_string(qaArg) == ""-qa-finish-round"") global.qaFinishRound = true;
var popup = instance_create(0, 0, objDirectIPPopup);
popup.isConnecting = true;
popup.alarm[0] = 1;
");
Edit("gml_Object_objOnlineControl_Create_0", code =>
    code.Replace("instance_create(0, 0, objGetMyIP);", ""));
Edit("gml_Object_objOnlineLobby_Create_0", code => code + @"
qaReadyTimer = 0;
global.qaCaptured = false;
global.qaSampled = false;
global.qaRound++;
show_debug_message(""Arena8 QA lobby "" + string(global.qaRound) + "" as P"" + string(global.onlinePlayerPort));
");
Edit("gml_Object_objOnlineLobby_Step_0", _ => @"
if (canUseMenu && global.onlinePlayerCount == 8 && global.onlinePlayerPort > 0 &&
    !global.onlineLobbyReady[global.onlinePlayerPort])
{
    qaReadyTimer++;
    if (qaReadyTimer == 25)
        sendDataToServerUDP([6, 1], [global.onlinePlayerPort, 1], [1, 4],
            [global.onlinePlayerPort - 1, 1], [0, 5], [0, 5], [0, 1], [0, 1]);
    if (qaReadyTimer > 45)
    {
        global.onlineLobbyReady[global.onlinePlayerPort] = true;
        canUseMenu = false;
        sendDataToServerUDP([14, 1], [global.onlinePlayerPort, 1]);
    }
}
");
Edit("gml_GlobalScript_mapControls", code => code.Replace("var alreadyHasInput = false;", @"
                if (global.matchStarted && global.onlineMatchFrame >= 220 && global.onlineMatchFrame < 257)
                {
                    keyRight = (thisPlayer mod 2) == 1;
                    keyLeft = !keyRight;
                }
                var alreadyHasInput = false;"));
Edit("gml_Object_objOnlineControl_Step_0", code => code + @"
if (room == rmWilyBoss && global.matchStarted && global.onlinePlayerCount == 8)
{
    if (!global.qaSampled && global.onlineMatchFrame >= 216)
    {
        global.qaSampled = true;
        global.qaPositions = array_create(9, -1);
        for (var qaSlot = 1; qaSlot <= 8; qaSlot++)
        {
            var fighter = findPlayerID(qaSlot);
            if (instance_exists(fighter)) global.qaPositions[qaSlot] = fighter.x;
        }
    }
    if (!global.qaCaptured && global.onlineMatchFrame >= 265)
    {
        global.qaCaptured = true;
        var present = 0;
        var moved = 0;
        var positions = """";
        for (var qaSlot = 1; qaSlot <= 8; qaSlot++)
        {
            var fighter = findPlayerID(qaSlot);
            if (instance_exists(fighter))
            {
                present++;
                positions += string(round(fighter.x)) + "","";
                if (abs(fighter.x - global.qaPositions[qaSlot]) > 1) moved++;
            }
        }
        show_debug_message(""Arena8 QA round "" + string(global.qaRound) + "" P"" + string(global.onlinePlayerPort) +
            "" fighters="" + string(present) + "" moved="" + string(moved) + "" X="" + positions);
        if (global.qaFinishRound && global.qaRound == 1 && global.onlinePlayerPort != 1)
            sendDataToServerUDP([28, 1], [global.onlinePlayerPort, 1]);
    }
}
");
var control = Data.GameObjects.Single(x => x.Name.Content == "objOnlineControl");
group.QueueReplace(control.EventHandlerFor(EventType.Draw, 75, Data), @"
if (room == rmWilyBoss && global.matchStarted && global.onlinePlayerPort == 1 &&
    global.onlineMatchFrame == 214 && global.qaRound == 1)
    screen_save(""Arena8-QA.png"");
");
group.Import();
