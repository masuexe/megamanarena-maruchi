using System.Net;
using Mma.Protocol;
using Mma.Server;

namespace Mma.Protocol.Tests;

public sealed class ServerEngineUnitTests
{
    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = startUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private static byte[] BuildControl(ushort sessionId, MmaPacketType type, Action<GmlBufferWriter> writeBody)
    {
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(sessionId, (byte)type).WriteTo(writer);
        writeBody(writer);
        return writer.ToArray();
    }

    private static ConnectPacket MakeConnect(
        string name,
        string version = MmaConstants.StockVersion,
        byte color = 0,
        short character = 1,
        bool observer = false,
        int udpPort = 0) => new(
        Version: version,
        ClientPort: 41000,
        ClientUdpPort: (uint)udpPort,
        IsObserver: observer,
        Name: name,
        CloudId: "cloud-" + name,
        Character: character,
        Color: color,
        CustomPrimaryColor: 0,
        CustomSecondaryColor: 0,
        Skin: 0,
        ArenaStage: "rmWilyBoss",
        BossStage: "rmMechaDragon",
        StageModeStage: "rmBombmanStageMode0",
        SlideDownJump: 1,
        CheatEnabled: false);

    private static void ConnectAndAck(MmaServerEngine engine, IPEndPoint ep, string name, byte expectedSlot, byte color = 0, string version = MmaConstants.StockVersion)
    {
        var welcomeOut = engine.HandleControlDatagram(ep, BuildControl(0, MmaPacketType.Connect, MakeConnect(name, version, color: color, udpPort: ep.Port + 1).WritePayload));
        var dgram = Assert.Single(welcomeOut);
        var reader = new GmlBufferReader(dgram.Payload);
        var hdr = MmaPacketHeader.ReadFrom(reader);
        Assert.Equal(MmaPacketType.NewPlayerWelcome, hdr.KnownType);
        var welcome = NewPlayerWelcomePacket.ReadPayload(reader, engine.Options.UsesArena8Protocol);
        Assert.Equal(expectedSlot, welcome.PlayerSlot);

        var ackOut = engine.HandleControlDatagram(ep, BuildControl(engine.SessionId, MmaPacketType.NewPlayerJoinAck, new NewPlayerJoinAckPacket(expectedSlot, false).WritePayload));
        Assert.True(ackOut.Count >= 9);
    }

    [Fact]
    public void ConnectValidation_RejectsBadVersionObserversFullAndStartedAndDuplicate()
    {
        var options = new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena");
        var engine = new MmaServerEngine(options, nextSessionId: () => 4242);

        var p1Ep = new IPEndPoint(IPAddress.Loopback, 40001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 40003);
        var p3Ep = new IPEndPoint(IPAddress.Loopback, 40005);

        // Wrong version -> DisallowConnect(VersionMismatch)
        var badVer = engine.HandleControlDatagram(p1Ep, BuildControl(0, MmaPacketType.Connect, MakeConnect("P1", "5.0.0.9999").WritePayload));
        var badVerReader = new GmlBufferReader(Assert.Single(badVer).Payload);
        _ = MmaPacketHeader.ReadFrom(badVerReader);
        Assert.Equal(DisallowConnectReason.VersionMismatch, DisallowConnectPacket.ReadPayload(badVerReader).Reason);

        // Observer disabled -> DisallowConnect(ObserversDisabled)
        var obs = engine.HandleControlDatagram(p1Ep, BuildControl(0, MmaPacketType.Connect, MakeConnect("Obs", observer: true).WritePayload));
        var obsReader = new GmlBufferReader(Assert.Single(obs).Payload);
        _ = MmaPacketHeader.ReadFrom(obsReader);
        Assert.Equal(DisallowConnectReason.ObserversDisabled, DisallowConnectPacket.ReadPayload(obsReader).Reason);

        ConnectAndAck(engine, p1Ep, "P1", 1);

        // Duplicate Connect from same (IP, ControlPort) is silently ignored.
        var dup = engine.HandleControlDatagram(p1Ep, BuildControl(0, MmaPacketType.Connect, MakeConnect("P1-Dup").WritePayload));
        Assert.Empty(dup);
        Assert.Equal(1, engine.ConnectedPlayerCount);

        ConnectAndAck(engine, p2Ep, "P2", 2);

        // Lobby full -> DisallowConnect(LobbyFull)
        var full = engine.HandleControlDatagram(p3Ep, BuildControl(0, MmaPacketType.Connect, MakeConnect("P3").WritePayload));
        var fullReader = new GmlBufferReader(Assert.Single(full).Payload);
        _ = MmaPacketHeader.ReadFrom(fullReader);
        Assert.Equal(DisallowConnectReason.LobbyFull, DisallowConnectPacket.ReadPayload(fullReader).Reason);
    }

    [Fact]
    public void SpoofGuardsAndHostOnlyBlocking_DropInvalidSendersAndPackets()
    {
        var options = new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena");
        var engine = new MmaServerEngine(options, nextSessionId: () => 5000);
        var p1Ep = new IPEndPoint(IPAddress.Loopback, 41001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 41003);

        ConnectAndAck(engine, p1Ep, "P1", 1);
        ConnectAndAck(engine, p2Ep, "P2", 2);

        // P2 spoofing slot 1 on CharChange, Ready, and Disconnect is ignored.
        Assert.Empty(engine.HandleControlDatagram(p2Ep, BuildControl(5000, MmaPacketType.CharChange, new CharChangePacket(1, 2, 1, 0, 0, 0, 0).WritePayload)));
        Assert.Empty(engine.HandleControlDatagram(p2Ep, BuildControl(5000, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload)));
        Assert.Empty(engine.HandleControlDatagram(p2Ep, BuildControl(5000, MmaPacketType.Disconnect, w => w.WriteU8(1))));

        // Wrong session ID is dropped.
        Assert.Empty(engine.HandleControlDatagram(p2Ep, BuildControl(9999, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload)));

        // Host-only packets 8, 12, 13, 32, 34 sent by clients are ignored.
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(5000, MmaPacketType.LobbyChange, new LobbyChangePacket(2, 0, false, false).WritePayload)));
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(5000, MmaPacketType.RulesUpdate, new RulesUpdatePacket(false, false, 1, 0, false, 840).WritePayload)));
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(5000, MmaPacketType.BufferChange, w => w.WriteU8(1))));
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(5000, MmaPacketType.TourneyRules, w => { w.WriteU8(3); w.WriteU8(1); })));
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(5000, MmaPacketType.TourneyStrike, w => w.WriteU8(2))));
    }

    [Fact]
    public void TeamsMode_RequiresDistinctTeamBucketsBeforeStartingMatch()
    {
        var options = new ServerOptions(IPAddress.Loopback, 6520, 2, "Teams");
        var engine = new MmaServerEngine(options, nextSessionId: () => 3333);
        var p1Ep = new IPEndPoint(IPAddress.Loopback, 42001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 42003);

        // Both on Red team (colors 0 and 2 both fall in color/3 == 0).
        ConnectAndAck(engine, p1Ep, "Red1", 1, color: 0);
        ConnectAndAck(engine, p2Ep, "Red2", 2, color: 2);

        engine.HandleControlDatagram(p1Ep, BuildControl(3333, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        var secondReady = engine.HandleControlDatagram(p2Ep, BuildControl(3333, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));

        Assert.Equal(LobbyPhase.Lobby, engine.Phase);
        foreach (var dgram in secondReady)
        {
            var r = new GmlBufferReader(dgram.Payload);
            var h = MmaPacketHeader.ReadFrom(r);
            Assert.Equal(MmaPacketType.PlayerReady, h.KnownType);
            Assert.False(ServerPlayerReadyPacket.ReadPayload(r).AllReady);
        }

        // Unready works because match hasn't locked in, switch P2 to Blue team (color 3 -> bucket 1), re-ready.
        Assert.NotEmpty(engine.HandleControlDatagram(p2Ep, BuildControl(3333, MmaPacketType.PlayerUnready, new PlayerUnreadyPacket(2).WritePayload)));
        Assert.NotEmpty(engine.HandleControlDatagram(p2Ep, BuildControl(3333, MmaPacketType.CharChange, new CharChangePacket(2, 1, 3, 0, 0, 0, 1).WritePayload)));
        var launchReady = engine.HandleControlDatagram(p2Ep, BuildControl(3333, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));

        Assert.Equal(LobbyPhase.StartingMatch, engine.Phase);
        var r2 = new GmlBufferReader(launchReady[0].Payload);
        _ = MmaPacketHeader.ReadFrom(r2);
        Assert.True(ServerPlayerReadyPacket.ReadPayload(r2).AllReady);

        // Unready is blocked once locked into StartingMatch.
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(3333, MmaPacketType.PlayerUnready, new PlayerUnreadyPacket(1).WritePayload)));
    }

    [Fact]
    public void SquadsMode_ValidatesFullSquadsAndRejectsPartialSquadReady()
    {
        var options = new ServerOptions(IPAddress.Loopback, 6520, 2, "Squads");
        var engine = new MmaServerEngine(options, nextSessionId: () => 4444);
        var p1Ep = new IPEndPoint(IPAddress.Loopback, 43001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 43003);

        ConnectAndAck(engine, p1Ep, "P1", 1);
        ConnectAndAck(engine, p2Ep, "P2", 2);

        // Cannot ready with empty squad.
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(4444, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload)));

        // Wrong squad length (!= 4) is dropped.
        Assert.Empty(engine.HandleControlDatagram(p1Ep, BuildControl(4444, MmaPacketType.SquadList, new SquadListPacket(1, 0, 1, [1, 2]).WritePayload)));

        // Valid 4-member squads accepted and allow Ready -> GotoLevel.
        Assert.NotEmpty(engine.HandleControlDatagram(p1Ep, BuildControl(4444, MmaPacketType.SquadList, new SquadListPacket(1, 0, 1, [1, 2, 3, 4]).WritePayload)));
        Assert.NotEmpty(engine.HandleControlDatagram(p2Ep, BuildControl(4444, MmaPacketType.SquadList, new SquadListPacket(2, 3, 1, [5, 6, 7, 8]).WritePayload)));

        engine.HandleControlDatagram(p1Ep, BuildControl(4444, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        var finalReady = engine.HandleControlDatagram(p2Ep, BuildControl(4444, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));
        Assert.Equal(LobbyPhase.StartingMatch, engine.Phase);
        Assert.Contains(finalReady, d => d.Payload[2] == (byte)MmaPacketType.SquadList);
    }

    [Fact]
    public void StartingMatchTimeout_AndMatchStallTimeout_BroadcastLobbyClosedAndResetRoom()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        var timings = new ServerTimingOptions(
            StartingMatchTimeout: TimeSpan.FromSeconds(10),
            MatchStallTimeout: TimeSpan.FromSeconds(15),
            TickInterval: TimeSpan.FromMilliseconds(50),
            LobbyHeartbeatInterval: TimeSpan.FromSeconds(3),
            FreezeRetransmitCooldown: TimeSpan.FromMilliseconds(12));
        var options = new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena");
        var engine = new MmaServerEngine(options, clock, timings, nextSessionId: () => 6001);

        var p1Ep = new IPEndPoint(IPAddress.Loopback, 44001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 44003);

        // Part 1: StartingMatch >10s deadline closes and resets room.
        ConnectAndAck(engine, p1Ep, "P1", 1);
        ConnectAndAck(engine, p2Ep, "P2", 2);
        engine.HandleControlDatagram(p1Ep, BuildControl(6001, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(6001, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));
        engine.HandleControlDatagram(p1Ep, BuildControl(6001, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmCutman").WritePayload));

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(engine.Tick());
        Assert.Equal(LobbyPhase.StartingMatch, engine.Phase);

        clock.Advance(TimeSpan.FromSeconds(2)); // 11s > 10s
        var closedStarting = engine.Tick();
        Assert.Equal(2, closedStarting.Count);
        Assert.All(closedStarting, d => Assert.Equal((byte)MmaPacketType.LobbyClosed, d.Payload[2]));
        Assert.Equal(LobbyPhase.Lobby, engine.Phase);
        Assert.Equal(0, engine.ConnectedPlayerCount);

        // Part 2: InMatch >15s single-player stall while peer stays active closes and resets room.
        ConnectAndAck(engine, p1Ep, "P1", 1);
        ConnectAndAck(engine, p2Ep, "P2", 2);
        engine.HandleControlDatagram(p1Ep, BuildControl(6001, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(6001, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));
        engine.HandleControlDatagram(p1Ep, BuildControl(6001, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmCutman").WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(6001, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmCutman").WritePayload));
        Assert.Equal(LobbyPhase.InMatch, engine.Phase);

        var p1InEp = new IPEndPoint(IPAddress.Loopback, p1Ep.Port + 1);
        for (int sec = 1; sec <= 15; sec++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var inWriter = new GmlBufferWriter();
            new ClientInputPacket(1, (uint)(sec * 60), [new InputFrameEntry((uint)(sec * 60 + 6), 0x80000000)]).WriteToFastInput(inWriter);
            var relayed = engine.HandleInputDatagram(p1InEp, inWriter.WrittenMemory);
            Assert.Single(relayed);
            if (sec <= 14)
            {
                var tickOut = engine.Tick();
                Assert.DoesNotContain(tickOut, d => d.Payload[2] == (byte)MmaPacketType.LobbyClosed);
                if (sec % 3 == 0)
                {
                    var probe = Assert.Single(tickOut);
                    Assert.Equal(p2Ep, probe.Target);
                    Assert.Equal((byte)MmaPacketType.GratuitousPing, probe.Payload[2]);
                }
                else
                {
                    Assert.Empty(tickOut);
                }

                Assert.Equal(LobbyPhase.InMatch, engine.Phase);
            }
        }

        // At exactly 15s it is not strictly >15s; advance 100ms past 15s while P1 stays active and P2 is stalled.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var stallClose = engine.Tick();
        Assert.Equal(2, stallClose.Count);
        Assert.All(stallClose, d =>
        {
            var r = new GmlBufferReader(d.Payload);
            var h = MmaPacketHeader.ReadFrom(r);
            Assert.Equal(MmaPacketType.LobbyClosed, h.KnownType);
            Assert.Equal("LOBBY_CLOSED", r.ReadGmlString());
        });
        Assert.Equal(LobbyPhase.Lobby, engine.Phase);
        Assert.Equal(0, engine.ConnectedPlayerCount);
    }

    [Fact]
    public void InputCachingAndFreezeRetransmits_DeduplicateBurstsAndRecoverFrozenClients()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        var options = new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena", Buffer: 6);
        var engine = new MmaServerEngine(options, clock, nextSessionId: () => 7100);
        var p1Ep = new IPEndPoint(IPAddress.Loopback, 45001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 45003);

        ConnectAndAck(engine, p1Ep, "P1", 1);
        ConnectAndAck(engine, p2Ep, "P2", 2);
        engine.HandleControlDatagram(p1Ep, BuildControl(7100, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(7100, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));
        engine.HandleControlDatagram(p1Ep, BuildControl(7100, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(7100, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload));

        var p1In = new IPEndPoint(IPAddress.Loopback, p1Ep.Port + 1);
        byte[] BuildFast(ClientInputPacket pkt)
        {
            var w = new GmlBufferWriter();
            pkt.WriteToFastInput(w);
            return w.ToArray();
        }

        // Duplicate entries within one packet deduplicate cleanly.
        var pkt1 = new ClientInputPacket(1, 10, [new InputFrameEntry(16, 1), new InputFrameEntry(16, 2), new InputFrameEntry(17, 3)]);
        var relay1 = engine.HandleInputDatagram(p1In, BuildFast(pkt1));
        var r1 = new GmlBufferReader(Assert.Single(relay1).Payload);
        Assert.Equal((byte)MmaPacketType.Input, r1.ReadU8());
        var relayedBody1 = RelayInputPacket.ReadBody(r1);
        Assert.Equal([new InputFrameEntry(16, 2), new InputFrameEntry(17, 3)], relayedBody1.Frames);
        Assert.Equal(2u, engine.GetCachedInputs(1)[16]);

        // Immediate micro-burst duplicate (<12ms) is suppressed.
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Empty(engine.HandleInputDatagram(p1In, BuildFast(pkt1)));

        // Frozen client retransmission (>=12ms later) IS relayed for freeze recovery.
        clock.Advance(TimeSpan.FromMilliseconds(16));
        Assert.Single(engine.HandleInputDatagram(p1In, BuildFast(pkt1)));

        // Empty count=0 freeze probe retransmits cached recent frames cleanly.
        clock.Advance(TimeSpan.FromMilliseconds(16));
        var emptyProbe = new ClientInputPacket(1, 10, []);
        var probeRelay = engine.HandleInputDatagram(p1In, BuildFast(emptyProbe));
        var probeReader = new GmlBufferReader(Assert.Single(probeRelay).Payload);
        Assert.Equal((byte)MmaPacketType.Input, probeReader.ReadU8());
        Assert.Equal(2, RelayInputPacket.ReadBody(probeReader).Frames.Count);
    }

    [Fact]
    public void SimultaneousConnects_DoNotSendLowerSlotNewPlayerDataToLaterSlots()
    {
        var options = new ServerOptions(IPAddress.Loopback, 6520, 3, "Arena");
        var engine = new MmaServerEngine(options, nextSessionId: () => 8800);
        var p1Ep = new IPEndPoint(IPAddress.Loopback, 46001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 46003);
        var p3Ep = new IPEndPoint(IPAddress.Loopback, 46005);

        engine.HandleControlDatagram(p1Ep, BuildControl(0, MmaPacketType.Connect, MakeConnect("P1").WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(0, MmaPacketType.Connect, MakeConnect("P2").WritePayload));
        engine.HandleControlDatagram(p3Ep, BuildControl(0, MmaPacketType.Connect, MakeConnect("P3").WritePayload));

        var ack1Out = engine.HandleControlDatagram(p1Ep, BuildControl(8800, MmaPacketType.NewPlayerJoinAck, new NewPlayerJoinAckPacket(1, false).WritePayload));
        var newPlayerDataRecipients = ack1Out
            .Where(d => d.Payload[2] == (byte)MmaPacketType.NewPlayerData)
            .Select(d => d.Target.Port)
            .ToArray();

        Assert.Equal([p1Ep.Port], newPlayerDataRecipients);
    }

    [Fact]
    public void PostMatchIdleP1_InOnlineUseInputRudpMode_IsProbedAndTransitionsToRound2BeforeStallTimeout()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        var timings = new ServerTimingOptions(
            StartingMatchTimeout: TimeSpan.FromSeconds(10),
            MatchStallTimeout: TimeSpan.FromSeconds(15),
            TickInterval: TimeSpan.FromMilliseconds(50),
            LobbyHeartbeatInterval: TimeSpan.FromSeconds(3),
            FreezeRetransmitCooldown: TimeSpan.FromMilliseconds(12));
        var engine = new MmaServerEngine(new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena"), clock, timings, nextSessionId: () => 6002);

        var p1Ep = new IPEndPoint(IPAddress.Loopback, 47001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 47003);

        ConnectAndAck(engine, p1Ep, "P1", 1);
        ConnectAndAck(engine, p2Ep, "P2", 2);
        engine.HandleControlDatagram(p1Ep, BuildControl(6002, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(6002, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));
        engine.HandleControlDatagram(p1Ep, BuildControl(6002, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(6002, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload));
        Assert.Equal(LobbyPhase.InMatch, engine.Phase);

        // Exchange RUDP control-port inputs (onlineUseInputRUDP = true), then P1 forfeits.
        byte[] p1InCtrl = BuildControl(6002, MmaPacketType.Input, new ClientInputPacket(1, 10, [new InputFrameEntry(16, 1)]).WriteBody);
        byte[] p2InCtrl = BuildControl(6002, MmaPacketType.Input, new ClientInputPacket(2, 10, [new InputFrameEntry(16, 2)]).WriteBody);
        Assert.Single(engine.HandleControlDatagram(p1Ep, p1InCtrl));
        Assert.Single(engine.HandleControlDatagram(p2Ep, p2InCtrl));
        Assert.Equal(2, engine.HandleControlDatagram(p1Ep, BuildControl(6002, MmaPacketType.Forfeit, new ForfeitPacket(1).WritePayload)).Count);

        // During post-forfeit replay (~2s simulated), lingering inputs keep both active and prevent premature probe.
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            _ = engine.HandleControlDatagram(p1Ep, p1InCtrl);
            _ = engine.HandleControlDatagram(p2Ep, p2InCtrl);
            Assert.Empty(engine.Tick());
        }

        // Clients return to rmOnlineLobby and stop inputs: P2 proactively sends GratuitousPing (23), while P1 stays silent.
        clock.Advance(TimeSpan.FromSeconds(1));
        var p2PingOut = engine.HandleControlDatagram(p2Ep, BuildControl(6002, MmaPacketType.GratuitousPing, w =>
        {
            w.WriteU8(2);
            w.WriteGmlString("GRATUITOUS_PING");
        }));
        Assert.Single(p2PingOut);
        Assert.Empty(engine.Tick()); // P1 silent for only 1s (<3s), P2 already ReturnedToLobby.

        // At 3s after P1's last input, server probes idle P1 (and ONLY P1) with GratuitousPing (23).
        clock.Advance(TimeSpan.FromSeconds(2));
        var probeOut = engine.Tick();
        var pingToP1 = Assert.Single(probeOut);
        Assert.Equal(p1Ep, pingToP1.Target);
        Assert.Equal((byte)MmaPacketType.GratuitousPing, pingToP1.Payload[2]);
        Assert.Equal(LobbyPhase.InMatch, engine.Phase);

        // P1 responds in rmOnlineLobby with GratuitousPingEcho (24) -> transitions cleanly to Lobby for Round 2!
        var echoOut = engine.HandleControlDatagram(p1Ep, BuildControl(6002, MmaPacketType.GratuitousPingEcho, w =>
        {
            w.WriteU8(1);
            w.WriteGmlString("GRATUITOUS_PING");
        }));
        Assert.Empty(echoOut);
        Assert.Equal(LobbyPhase.Lobby, engine.Phase);

        // Even advancing further never hits MatchStallTimeout; P1 & P2 start Round 2 cleanly.
        engine.HandleControlDatagram(p1Ep, BuildControl(6002, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        var r2Ready = engine.HandleControlDatagram(p2Ep, BuildControl(6002, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));
        Assert.Equal(LobbyPhase.StartingMatch, engine.Phase);
        Assert.Contains(r2Ready, d => d.Payload[2] == (byte)MmaPacketType.PlayerReady);

        engine.HandleControlDatagram(p1Ep, BuildControl(6002, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload));
        engine.HandleControlDatagram(p2Ep, BuildControl(6002, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload));
        Assert.Equal(LobbyPhase.InMatch, engine.Phase);
    }

    [Fact]
    public void PartialLobbyReady_StartsMatchWhenAtLeastTwoConnectedPlayersAreAllReady()
    {
        var options = new ServerOptions(IPAddress.Loopback, 6520, 4, "Arena");
        var engine = new MmaServerEngine(options, nextSessionId: () => 9101);
        var p1Ep = new IPEndPoint(IPAddress.Loopback, 48001);
        var p2Ep = new IPEndPoint(IPAddress.Loopback, 48003);

        ConnectAndAck(engine, p1Ep, "P1", 1);

        var p1OnlyReady = engine.HandleControlDatagram(p1Ep, BuildControl(9101, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload));
        var r1 = new GmlBufferReader(Assert.Single(p1OnlyReady).Payload);
        _ = MmaPacketHeader.ReadFrom(r1);
        Assert.False(ServerPlayerReadyPacket.ReadPayload(r1).AllReady);
        Assert.Equal(LobbyPhase.Lobby, engine.Phase);

        ConnectAndAck(engine, p2Ep, "P2", 2);
        var p2Ready = engine.HandleControlDatagram(p2Ep, BuildControl(9101, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload));
        Assert.Equal(LobbyPhase.StartingMatch, engine.Phase);
        Assert.Equal(2, engine.ConnectedPlayerCount);
        foreach (var dgram in p2Ready)
        {
            var r = new GmlBufferReader(dgram.Payload);
            var h = MmaPacketHeader.ReadFrom(r);
            Assert.Equal(MmaPacketType.PlayerReady, h.KnownType);
            Assert.True(ServerPlayerReadyPacket.ReadPayload(r).AllReady);
        }

        // Only 2 connected players in a 4-slot room must also launch GotoLevel on MatchReady.
        var g1 = engine.HandleControlDatagram(p1Ep, BuildControl(9101, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload));
        Assert.Empty(g1);
        var g2 = engine.HandleControlDatagram(p2Ep, BuildControl(9101, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload));
        Assert.Contains(g2, d => d.Payload[2] == (byte)MmaPacketType.GotoLevel);
        Assert.Equal(LobbyPhase.InMatch, engine.Phase);
    }

    [Fact]
    public void DeadlineTimeline_SingleStallDepartsCleanly_StagePhase2RetiresHistory_AndRound2CompactsSlots()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero));
        var timings = new ServerTimingOptions(
            StartingMatchTimeout: TimeSpan.FromSeconds(10),
            MatchStallTimeout: TimeSpan.FromSeconds(5),
            TickInterval: TimeSpan.FromMilliseconds(16),
            LobbyHeartbeatInterval: TimeSpan.FromSeconds(3),
            FreezeRetransmitCooldown: TimeSpan.FromMilliseconds(12));
        var options = new ServerOptions(IPAddress.Loopback, 6528, 5, "Arena8", Buffer: 6);
        const ulong fixedSeed = 0x12345678UL;
        var engine = new MmaServerEngine(options, clock, timings, nextSessionId: () => 9500, nextRandomSeed: () => fixedSeed);

        var endpoints = Enumerable.Range(0, 5)
            .Select(i => new IPEndPoint(IPAddress.Loopback, 49100 + (i * 2)))
            .ToArray();
        for (int i = 0; i < 5; i++)
        {
            ConnectAndAck(engine, endpoints[i], $"P{i + 1}", (byte)(i + 1), color: (byte)i, version: MmaConstants.Arena8Version);
        }

        for (int i = 0; i < 5; i++)
        {
            engine.HandleControlDatagram(endpoints[i], BuildControl(9500, MmaPacketType.PlayerReady, new ClientReadyPacket((byte)(i + 1)).WritePayload));
        }

        for (int i = 0; i < 5; i++)
        {
            engine.HandleControlDatagram(endpoints[i], BuildControl(9500, MmaPacketType.GotoLevel, new MatchReadyPacket((byte)(i + 1), "rmWilyBoss").WritePayload));
        }
        Assert.Equal(LobbyPhase.InMatch, engine.Phase);

        byte[] BuildProposal(uint token, ushort phase, uint ack, params InputFrameEntry[] entries)
        {
            var w = new GmlBufferWriter();
            new DeadlineProposalPacket(token, phase, ack, entries).WriteTo(w, 9500);
            return w.ToArray();
        }

        const uint token = 0x12345678u;
        // Players 1..4 send proposals for frames 7..9; P5 stays silent (lagging/crashed).
        for (int i = 0; i < 4; i++)
        {
            Assert.Empty(engine.HandleControlDatagram(
                endpoints[i],
                BuildProposal(token, phase: 1, ack: 6, new InputFrameEntry(9, (uint)(i + 10)))));
        }

        // Initial ConfirmLead (= 2) commits 7 and 8 immediately on first tick; 17ms later commits 9 with P5=0.
        var t0Out = engine.Tick();
        Assert.NotEmpty(t0Out);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        var t1Out = engine.Tick();
        var p1Batch = Assert.Single(t1Out, d => d.Target.Equals(endpoints[0]) && d.Payload[2] == (byte)MmaPacketType.DeadlineTimeline);
        Assert.True(DeadlineBatchPacket.TryReadPayload(p1Batch.Payload, 5, out DeadlineBatchPacket? batch9));
        Assert.NotNull(batch9);
        Assert.True(batch9.HighestCommitted >= 9u);
        DeadlineFrameSnapshot snap9 = Assert.Single(batch9.Frames, f => f.FrameId == 9u);
        Assert.Equal([10u, 11u, 12u, 13u, 0u], snap9.PlayerMasks);

        // Phase 1 -> Phase 2 (e.g. Stage mode entering boss room): P1 proposes phase=2.
        Assert.Empty(engine.HandleControlDatagram(endpoints[0], BuildProposal(token, phase: 2, ack: 6, new InputFrameEntry(9, 77u))));
        // Lagging P4 still requests retired Phase 1 repair from ack=6 -> gets direct retired batch!
        var repairOut = engine.HandleControlDatagram(endpoints[3], BuildProposal(token, phase: 1, ack: 6));
        var repairDgram = Assert.Single(repairOut);
        Assert.True(DeadlineBatchPacket.TryReadPayload(repairDgram.Payload, 5, out DeadlineBatchPacket? retiredBatch));
        Assert.Equal((ushort)1, retiredBatch!.Phase);
        Assert.Equal(9u, retiredBatch.HighestCommitted);
        Assert.Equal([7u, 8u, 9u], retiredBatch.Frames.Select(f => f.FrameId).ToArray());

        // Advance past MatchStallTimeout (5.5s) keeping P1..P4 alive with heartbeats while P5 never responds.
        for (int s = 0; s < 6; s++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            for (int i = 0; i < 4; i++)
            {
                engine.HandleControlDatagram(endpoints[i], BuildProposal(token, phase: 2, ack: 6));
            }
            var tickOut = engine.Tick();
            Assert.DoesNotContain(tickOut, d => d.Payload[2] == (byte)MmaPacketType.LobbyClosed);
        }
        Assert.Equal(LobbyPhase.InMatch, engine.Phase);

        // P1..P4 finish round and return to lobby -> departed P5 is removed & slots compact cleanly to 4!
        for (int i = 0; i < 4; i++)
        {
            var pingOut = engine.HandleControlDatagram(endpoints[i], BuildControl(9500, MmaPacketType.GratuitousPing, w =>
            {
                w.WriteU8((byte)(i + 1));
                w.WriteGmlString("GRATUITOUS_PING");
            }));
            if (i == 3)
            {
                Assert.Contains(pingOut, d => d.Payload[2] == (byte)MmaPacketType.ChangePlayerPort);
                Assert.Contains(pingOut, d => d.Payload[2] == (byte)MmaPacketType.ChangePlayerData);
            }
        }
        Assert.Equal(LobbyPhase.Lobby, engine.Phase);
        Assert.Equal(4, engine.ConnectedPlayerCount);
    }
}
