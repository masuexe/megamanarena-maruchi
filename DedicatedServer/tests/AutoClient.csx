// Test copy only: drive the stock client via its own connect and ready actions.
using System;
using System.Linq;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;

EnsureDataLoaded();
var group = new CodeImportGroup(Data, null, Data.ToolInfo.DecompilerSettings);
var dc = new GlobalDecompileContext(Data);
void Edit(string name, Func<string, string> patch)
{
    var entry = Data.Code.Single(x => x.Name.Content == name);
    var code = new Underanalyzer.Decompiler.DecompileContext(dc, entry, Data.ToolInfo.DecompilerSettings).DecompileToString();
    group.QueueReplace(entry, patch(code.Replace("\r\n", "\n")));
}

Data.GeneralInfo.Name = Data.Strings.MakeString("MegaManArenaDedicatedTest");
Data.GeneralInfo.FileName = Data.Strings.MakeString("MegaManArenaDedicatedTest");
Edit("gml_Object_objFighterInit_Create_0", code => code + @"
audio_master_gain(0);
room_goto(rmMenu);
");
Edit("gml_Object_objMainMenuControl_Create_0", code => code + @"
global.online = true;
global.serverIP = ""127.0.0.1"";
global.isUsingGMPunch = false;
global.onlineObserver = false;
global.mmaQaRounds = 0;
global.mmaQaPlayers = 2;
for (var argIndex = 1; argIndex <= parameter_count(); argIndex++)
{
    var flag = parameter_string(argIndex);
    if (string_pos(""-mma-players="", flag) == 1)
        global.mmaQaPlayers = real(string_delete(flag, 1, 13));
}
onlineJoinServer(global.serverIP, global.defaultServerPort, global.defaultClientPort, global.defaultClientPort + 1);
");
Edit("gml_Object_objOnlineControl_Create_0", code =>
    code.Replace("instance_create(0, 0, objGetMyIP);", ""));
Edit("gml_Object_objOnlineLobby_Create_0", code => code + @"
show_debug_message(""QA lobby: P"" + string(global.onlinePlayerPort));
global.mmaQaReported = false;
debugCounterReady = 0;
");
Edit("gml_Object_objOnlineLobby_Step_0", code => @"
if (canUseMenu && global.onlinePlayerCount == global.mmaQaPlayers && global.onlinePlayerPort > 0 &&
    !global.onlineLobbyReady[global.onlinePlayerPort])
{
    debugCounterReady++;
    if (debugCounterReady == 40)
    {
        if (global.submode == 2)
        {
            var color = (global.onlinePlayerPort <= (global.mmaQaPlayers div 2)) ? 0 : 3;
            sendDataToServerUDP([6, 1], [global.onlinePlayerPort, 1], [1, 4], [color, 1],
                [0, 5], [0, 5], [0, 1], [0, 1]);
        }
        else if (global.submode == 1)
        {
            var savedSquad = global.squadList[1];
            global.squadList[1] = array_create(global.stockCount, 1);
            sendSquadList(""server"", global.onlinePlayerPort);
            global.squadList[1] = savedSquad;
        }
    }
    if (debugCounterReady > 60)
    {
        global.onlineLobbyReady[global.onlinePlayerPort] = true;
        canUseMenu = false;
        sendDataToServerUDP([14, 1], [global.onlinePlayerPort, 1]);
        show_debug_message(""QA ready as P"" + string(global.onlinePlayerPort));
    }
}
");
Edit("gml_Object_objOnlineControl_Step_0", code => code + @"
if ((instance_exists(objMatchControl) || instance_exists(objMatchControlBoss)) &&
    !global.mmaQaReported && global.onlineMatchFrame >= 180)
{
    global.mmaQaReported = true;
    global.mmaQaRounds++;
    show_debug_message(""QA advanced match "" + string(global.mmaQaRounds) +
        "" as P"" + string(global.onlinePlayerPort) + "" submode "" + string(global.submode) +
        "" in "" + room_get_name(room) + "" to frame "" + string(global.onlineMatchFrame));
    if (global.onlineMode == 810 && global.mmaQaPlayers == 2 && global.onlinePlayerPort == 2)
        sendDataToServerUDP([28, 1], [global.onlinePlayerPort, 1]);
}
");
group.Import();
