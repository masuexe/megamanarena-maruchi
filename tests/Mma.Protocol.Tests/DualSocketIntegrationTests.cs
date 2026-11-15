using System.Net;
using System.Net.Sockets;
using System.Text;
using Mma.Protocol;
using Mma.Server;

namespace Mma.Protocol.Tests;

public sealed class DualSocketIntegrationTests
{
    private sealed class TestClientPeer : IAsyncDisposable
    {
        public TestClientPeer(string name, string version = MmaConstants.StockVersion, byte color = 0, short character = 1)
        {
            Name = name;
            Version = version;
            Color = color;
            Character = character;

            ControlUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            InputUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            ControlPort = ((IPEndPoint)ControlUdp.Client.LocalEndPoint!).Port;
            InputPort = ((IPEndPoint)InputUdp.Client.LocalEndPoint!).Port;
        }

        public string Name { get; }
        public string Version { get; }
        public byte Color { get; }
        public short Character { get; }
        public UdpClient ControlUdp { get; }
        public UdpClient InputUdp { get; }
        public int ControlPort { get; }
        private uint _sendSeq;
        private readonly HashSet<uint> _receivedServerSeqs = [];

        public int InputPort { get; }
        public ushort SessionId { get; private set; }
        public byte Slot { get; set; }
        public HashSet<uint> AckedByServerSeqs { get; } = [];
        public int ReceivedRudpDataFrames { get; private set; }
        public int ReceivedRudpAckFrames { get; private set; }
        public uint? LastClientAckSeq { get; private set; }

        public async Task<(MmaPacketHeader Header, byte[] Body)> ReceiveControlAsync(CancellationToken ct)
        {
            while (true)
            {
                UdpReceiveResult res = await ControlUdp.ReceiveAsync(ct);
                Assert.True(
                    GmRudpCodec.TryDecode(res.Buffer, out GmRudpFrame frame),
                    "Every binary datagram on Control Port must have valid 12-byte GM RUDP framing and ~CRC32 checksum.");

                if (frame.Kind == GmRudpKind.Ack)
                {
                    ReceivedRudpAckFrames++;
                    foreach (uint acked in frame.AckedSequences)
                    {
                        AckedByServerSeqs.Add(acked);
                    }

                    continue;
                }

                ReceivedRudpDataFrames++;
                uint ackSeq = Interlocked.Increment(ref _sendSeq);
                LastClientAckSeq = ackSeq;
                byte[] ackBytes = GmRudpCodec.EncodeAck(ackSeq, [frame.SequenceNumber]);
                await ControlUdp.SendAsync(ackBytes, res.RemoteEndPoint, ct);

                if (!_receivedServerSeqs.Add(frame.SequenceNumber))
                {
                    continue;
                }

                var reader = new GmlBufferReader(frame.Payload);
                var header = MmaPacketHeader.ReadFrom(reader);
                return (header, frame.Payload.Span[reader.Offset..].ToArray());
            }
        }

        public async Task<(MmaPacketType Type, byte[] Body)> ReceiveFastInputAsync(CancellationToken ct)
        {
            UdpReceiveResult res = await InputUdp.ReceiveAsync(ct);
            var type = (MmaPacketType)res.Buffer[0];
            return (type, res.Buffer[1..]);
        }

        public async Task<MmaPacketHeader> ReadUntilControlAsync(
            MmaPacketType targetType,
            Action<byte[]>? onMatched = null,
            CancellationToken ct = default)
        {
            while (true)
            {
                var (header, body) = await ReceiveControlAsync(ct);
                if (header.KnownType == targetType)
                {
                    onMatched?.Invoke(body);
                    return header;
                }
            }
        }

        public Task SendControlAsync(IPEndPoint serverControlEp, MmaPacketType type, Action<GmlBufferWriter> writeBody, CancellationToken ct)
        {
            var writer = new GmlBufferWriter();
            new MmaPacketHeader(SessionId, (byte)type).WriteTo(writer);
            writeBody(writer);
            uint seq = Interlocked.Increment(ref _sendSeq);
            byte[] framed = GmRudpCodec.EncodeData(seq, writer.WrittenMemory.Span);
            return ControlUdp.SendAsync(framed, serverControlEp, ct).AsTask();
        }

        public async Task<NewPlayerWelcomePacket> ConnectAndJoinAckAsync(
            IPEndPoint serverControlEp,
            bool arena8Format,
            CancellationToken ct)
        {
            var connect = new ConnectPacket(
                Version,
                (uint)ControlPort,
                (uint)InputPort,
                false,
                Name,
                "cloud-" + Name,
                Character,
                Color,
                0,
                0,
                0,
                "rmCutman",
                "rmGutsmanBoss",
                "rmCutmanStageMode0",
                1,
                false);
            await SendControlAsync(serverControlEp, MmaPacketType.Connect, connect.WritePayload, ct);

            NewPlayerWelcomePacket? welcome = null;
            await ReadUntilControlAsync(MmaPacketType.NewPlayerWelcome, body =>
            {
                var reader = new GmlBufferReader(body);
                welcome = NewPlayerWelcomePacket.ReadPayload(reader, arena8Format);
                Assert.Equal(0, reader.Remaining);
            }, ct);

            SessionId = welcome!.AssignedSessionId;
            Slot = welcome.PlayerSlot;

            await SendControlAsync(
                serverControlEp,
                MmaPacketType.NewPlayerJoinAck,
                new NewPlayerJoinAckPacket(Slot, false).WritePayload,
                ct);
            return welcome;
        }

        public async Task<GotoLevelPacket> ReadJoinSyncThroughNewPlayerDataAsync(ushort expectedOnlineMode, byte expectedSubmode, CancellationToken ct)
        {
            int ackCount = 0;
            bool sawRules = false;
            bool sawLobbyChange = false;
            bool sawPing = false;
            while (ackCount < 6 || !sawRules || !sawLobbyChange || !sawPing)
            {
                var (header, body) = await ReceiveControlAsync(ct);
                var reader = new GmlBufferReader(body);
                switch (header.KnownType)
                {
                    case MmaPacketType.ConnectAck:
                        ackCount++;
                        Assert.Equal(expectedOnlineMode, ConnectAckPacket.ReadPayload(reader).OnlineMode);
                        break;
                    case MmaPacketType.RulesUpdate:
                        sawRules = true;
                        _ = RulesUpdatePacket.ReadPayload(reader);
                        break;
                    case MmaPacketType.LobbyChange:
                        sawLobbyChange = true;
                        Assert.Equal(expectedSubmode, LobbyChangePacket.ReadPayload(reader).Submode);
                        break;
                    case MmaPacketType.Ping:
                        sawPing = true;
                        break;
                }
            }

            // Wait until self NewPlayerData arrives as well.
            while (true)
            {
                var (hdr, body) = await ReceiveControlAsync(ct);
                if (hdr.KnownType == MmaPacketType.NewPlayerData)
                {
                    var npd = NewPlayerDataPacket.ReadPayload(new GmlBufferReader(body));
                    if (npd.PlayerSlot == Slot)
                    {
                        break;
                    }
                }
            }

            return null!;
        }

        public async Task<GotoLevelPacket> ExpectGotoLevelAsync(CancellationToken ct)
        {
            GotoLevelPacket? result = null;
            await ReadUntilControlAsync(MmaPacketType.GotoLevel, body =>
            {
                result = GotoLevelPacket.ReadPayload(new GmlBufferReader(body));
            }, ct);
            return result!;
        }

        public ValueTask DisposeAsync()
        {
            ControlUdp.Dispose();
            InputUdp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Arena_FullLifecycle_DualSocketRelay_Forfeit_AndRound2Reset()
    {
        var options = new ServerOptions(IPAddress.Loopback, 0, 2, "Arena", Buffer: 6);
        using var host = new UdpServerHost(options, TextWriter.Null);
        Assert.Equal(host.BoundPort + 1, host.BoundInputPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = host.RunAsync(cts.Token);

        var serverControl = new IPEndPoint(IPAddress.Loopback, host.BoundPort);
        var serverInput = new IPEndPoint(IPAddress.Loopback, host.BoundInputPort);

        // Health probe
        using (var probe = new UdpClient())
        {
            await probe.SendAsync(Encoding.ASCII.GetBytes(MmaConstants.ProbePing), serverControl, cts.Token);
            var pong = await probe.ReceiveAsync(cts.Token);
            Assert.Equal("MMA_PONG:Arena:2", Encoding.ASCII.GetString(pong.Buffer));
        }

        await using var p1 = new TestClientPeer("Mega", color: 0);
        await using var p2 = new TestClientPeer("Proto", color: 3);

        var w1 = await p1.ConnectAndJoinAckAsync(serverControl, arena8Format: false, cts.Token);
        Assert.Equal(1, w1.PlayerSlot);
        Assert.Empty(w1.ExistingPlayers);
        await p1.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);

        var w2 = await p2.ConnectAndJoinAckAsync(serverControl, arena8Format: false, cts.Token);
        Assert.Equal(2, w2.PlayerSlot);
        Assert.Single(w2.ExistingPlayers);
        await p2.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);

        // Drain P1's notification of P2 joining.
        await p1.ReadUntilControlAsync(MmaPacketType.NewPlayerData, body =>
        {
            var data = NewPlayerDataPacket.ReadPayload(new GmlBufferReader(body));
            Assert.Equal(2, data.PlayerSlot);
            Assert.Equal("Proto", data.Summary.Name);
        }, cts.Token);

        // Lobby customization, unready before lock-in, then ready both.
        await p1.SendControlAsync(serverControl, MmaPacketType.CharChange, new CharChangePacket(1, 5, 1, 0xAA, 0xBB, 2, 1).WritePayload, cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.CharChange, body => Assert.Equal(5, CharChangePacket.ReadPayload(new GmlBufferReader(body)).Character), cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.CharChange, null, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.PlayerReady, body => Assert.False(ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady), cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.PlayerReady, null, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerUnready, new PlayerUnreadyPacket(1).WritePayload, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.PlayerUnready, body => Assert.Equal(1, PlayerUnreadyPacket.ReadPayload(new GmlBufferReader(body)).PlayerSlot), cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.PlayerUnready, null, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.PlayerReady, body => Assert.False(ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady), cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.PlayerReady, body => Assert.True(ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady), cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.PlayerReady, body => Assert.False(ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady), cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.PlayerReady, body => Assert.True(ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady), cts.Token);

        // Both confirm MatchReady -> GotoLevel
        await p1.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmCutman").WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmElecman").WritePayload, cts.Token);

        GotoLevelPacket goto1 = await p1.ExpectGotoLevelAsync(cts.Token);
        GotoLevelPacket goto2 = await p2.ExpectGotoLevelAsync(cts.Token);
        Assert.Equal("rmWilyBoss", goto1.Stage);
        Assert.Equal(goto1.RandomSeed, goto2.RandomSeed);
        Assert.NotEqual(0UL, goto1.RandomSeed);
        Assert.Equal(6, goto1.Buffer);
        Assert.Equal(LobbyPhase.InMatch, host.Engine.Phase);

        // Fast Input PortDiscovery (17 -> 18) & InputPing (26) on BoundInputPort
        var discWriter = new GmlBufferWriter();
        discWriter.WriteU8((byte)MmaPacketType.PortDiscovery);
        discWriter.WriteU8(1);
        await p1.InputUdp.SendAsync(discWriter.WrittenMemory, serverInput, cts.Token);
        var (discType, _) = await p1.ReceiveFastInputAsync(cts.Token);
        Assert.Equal(MmaPacketType.PortDiscoveryAck, discType);

        var pingWriter = new GmlBufferWriter();
        pingWriter.WriteU8((byte)MmaPacketType.InputPing);
        pingWriter.WriteGmlString("INPUT_PING");
        await p2.InputUdp.SendAsync(pingWriter.WrittenMemory, serverInput, cts.Token);

        // Fast Input Relay (Port + 1, NO ClientMatchFrame in S->C)
        var in1Writer = new GmlBufferWriter();
        new ClientInputPacket(1, 50, [new InputFrameEntry(56, 0x88000000)]).WriteToFastInput(in1Writer);
        await p1.InputUdp.SendAsync(in1Writer.WrittenMemory, serverInput, cts.Token);

        var (relayType, relayBody) = await p2.ReceiveFastInputAsync(cts.Token);
        Assert.Equal(MmaPacketType.Input, relayType);
        var relayedInput = RelayInputPacket.ReadBody(new GmlBufferReader(relayBody));
        Assert.Equal(1, relayedInput.Slot);
        Assert.Equal([new InputFrameEntry(56, 0x88000000)], relayedInput.Frames);

        // In-match Forfeit (28 -> 29)
        await p2.SendControlAsync(serverControl, MmaPacketType.Forfeit, new ForfeitPacket(2).WritePayload, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.MatchForfeit, body =>
        {
            var forfeit = MatchForfeitPacket.ReadPayload(new GmlBufferReader(body));
            Assert.Equal(2, forfeit.PlayerSlot);
            Assert.Equal(58u, forfeit.FrameId); // 50 + 6 + 2
        }, cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.MatchForfeit, null, cts.Token);

        // Natural round completion: both return to rmOnlineLobby & send GratuitousPing (23) -> Round 2 transition without LOBBY_CLOSED!
        await p1.SendControlAsync(serverControl, MmaPacketType.GratuitousPing, w => { w.WriteU8(1); w.WriteGmlString("GRATUITOUS_PING"); }, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.GratuitousPing, w => { w.WriteU8(2); w.WriteGmlString("GRATUITOUS_PING"); }, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.GratuitousPingEcho, null, cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.GratuitousPingEcho, null, cts.Token);
        Assert.Equal(LobbyPhase.Lobby, host.Engine.Phase);

        // Round 2: ready & start cleanly with same session & slots, then test RUDP control-port input relay!
        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.PlayerReady, null, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.PlayerReady, body => Assert.True(ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady), cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.PlayerReady, null, cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.PlayerReady, body => Assert.True(ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady), cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload, cts.Token);
        var round2Goto = await p2.ExpectGotoLevelAsync(cts.Token);
        Assert.Equal("rmWilyBoss", round2Goto.Stage);
        await p1.ExpectGotoLevelAsync(cts.Token);

        // Also verify RUDP Control-Port input relay (P2 -> P1 via Control socket -> receives with SessionId on P1 ControlUdp!)
        await p1.SendControlAsync(serverControl, MmaPacketType.Input, w => new ClientInputPacket(1, 0, [new InputFrameEntry(6, 1)]).WriteBody(w), cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.Input, w => new ClientInputPacket(2, 0, [new InputFrameEntry(6, 42)]).WriteBody(w), cts.Token);

        await p1.ReadUntilControlAsync(MmaPacketType.Input, body =>
        {
            var r = RelayInputPacket.ReadBody(new GmlBufferReader(body));
            Assert.Equal(2, r.Slot);
            Assert.Equal([new InputFrameEntry(6, 42)], r.Frames);
        }, cts.Token);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task DisconnectCompactsLobbySlots_AndClosesRoomWhenInMatch()
    {
        var options = new ServerOptions(IPAddress.Loopback, 0, 3, "Arena");
        using var host = new UdpServerHost(options, TextWriter.Null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = host.RunAsync(cts.Token);
        var serverControl = new IPEndPoint(IPAddress.Loopback, host.BoundPort);

        await using var p1 = new TestClientPeer("One");
        await using var p2 = new TestClientPeer("Two");
        await using var p3 = new TestClientPeer("Three");

        await p1.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p1.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);
        await p2.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p2.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);
        await p3.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p3.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.NewPlayerData, null, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.NewPlayerData, null, cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.NewPlayerData, null, cts.Token);

        // P2 leaves cleanly in lobby -> P3 compacts to slot 2!
        await p2.SendControlAsync(serverControl, MmaPacketType.Disconnect, w => w.WriteU8(2), cts.Token);
        await p3.ReadUntilControlAsync(MmaPacketType.ChangePlayerPort, body =>
        {
            var portUpdate = ChangePlayerPortPacket.ReadPayload(new GmlBufferReader(body));
            Assert.Equal(2, portUpdate.AssignedSlot);
            Assert.Equal(2, portUpdate.PlayerCount);
        }, cts.Token);
        p3.Slot = 2;

        // Fresh P3 fills compacted slot 3, start match, then P1 disconnects mid-match -> 25 LOBBY_CLOSED!
        await using var p3Replacement = new TestClientPeer("Replacement");
        var wRepl = await p3Replacement.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        Assert.Equal(3, wRepl.PlayerSlot);
        Assert.Equal(2, wRepl.ExistingPlayers.Count);
        await p3Replacement.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p3.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload, cts.Token);
        await p3Replacement.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(3).WritePayload, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload, cts.Token);
        await p3.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload, cts.Token);
        await p3Replacement.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(3, "rmWilyBoss").WritePayload, cts.Token);
        await p3.ExpectGotoLevelAsync(cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.Disconnect, w => w.WriteU8(1), cts.Token);
        await p3.ReadUntilControlAsync(MmaPacketType.LobbyClosed, body => Assert.Equal("LOBBY_CLOSED", new GmlBufferReader(body).ReadGmlString()), cts.Token);
        await p3Replacement.ReadUntilControlAsync(MmaPacketType.LobbyClosed, null, cts.Token);
        Assert.Equal(0, host.Engine.ConnectedPlayerCount);
        Assert.Equal(LobbyPhase.Lobby, host.Engine.Phase);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task Arena8_EightPlayers_HandshakeWelcomeFormat_AndRelayFanout()
    {
        var options = new ServerOptions(IPAddress.Loopback, 0, 8, "Arena8", Buffer: 6);
        using var host = new UdpServerHost(options, TextWriter.Null);
        Assert.Equal(host.BoundPort + 1, host.BoundInputPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task serverTask = host.RunAsync(cts.Token);
        var serverControl = new IPEndPoint(IPAddress.Loopback, host.BoundPort);
        var serverInput = new IPEndPoint(IPAddress.Loopback, host.BoundInputPort);

        // Stock 4P client trying to connect to Arena8 gets VersionMismatch (1).
        await using (var wrongVer = new TestClientPeer("WrongVer", MmaConstants.StockVersion))
        {
            var badConnect = new ConnectPacket(MmaConstants.StockVersion, (uint)wrongVer.ControlPort, (uint)wrongVer.InputPort, false, "Stock", "c", 1, 0, 0, 0, 0, "rmWilyBoss", "rmMechaDragon", "rmBombmanStageMode0", 1, false);
            await wrongVer.SendControlAsync(serverControl, MmaPacketType.Connect, badConnect.WritePayload, cts.Token);
            await wrongVer.ReadUntilControlAsync(MmaPacketType.DisallowConnect, body =>
            {
                Assert.Equal(DisallowConnectReason.VersionMismatch, DisallowConnectPacket.ReadPayload(new GmlBufferReader(body)).Reason);
            }, cts.Token);
        }

        var peers = new TestClientPeer[8];
        try
        {
            for (int i = 0; i < 8; i++)
            {
                peers[i] = new TestClientPeer($"P{i + 1}", MmaConstants.Arena8Version, color: (byte)i);
                var welcome = await peers[i].ConnectAndJoinAckAsync(serverControl, arena8Format: true, cts.Token);
                Assert.Equal(i + 1, welcome.PlayerSlot);
                Assert.Equal(i, welcome.ExistingPlayers.Count);
                await peers[i].ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);
            }

            // Drain earlier NewPlayerData broadcasts before checking Ready -> GotoLevel.
            for (int i = 0; i < 8; i++)
            {
                await peers[i].SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket((byte)(i + 1)).WritePayload, cts.Token);
            }

            for (int i = 0; i < 8; i++)
            {
                bool sawAllReady = false;
                while (!sawAllReady)
                {
                    await peers[i].ReadUntilControlAsync(MmaPacketType.PlayerReady, body =>
                    {
                        if (ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body)).AllReady)
                        {
                            sawAllReady = true;
                        }
                    }, cts.Token);
                }
            }

            Assert.Equal(LobbyPhase.StartingMatch, host.Engine.Phase);
            for (int i = 0; i < 8; i++)
            {
                await peers[i].SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket((byte)(i + 1), "rmWilyBoss").WritePayload, cts.Token);
            }

            for (int i = 0; i < 8; i++)
            {
                var gotoLevel = await peers[i].ExpectGotoLevelAsync(cts.Token);
                Assert.Equal("rmWilyBoss", gotoLevel.Stage);
            }

            Assert.Equal(LobbyPhase.InMatch, host.Engine.Phase);

            // Player 1 sends fast input on Port+1 -> all 7 other players P2..P8 receive it without ClientMatchFrame!
            var inWriter = new GmlBufferWriter();
            new ClientInputPacket(1, 200, [new InputFrameEntry(206, 0xCAFEBABEU)]).WriteToFastInput(inWriter);
            await peers[0].InputUdp.SendAsync(inWriter.WrittenMemory, serverInput, cts.Token);

            for (int i = 1; i < 8; i++)
            {
                var (type, body) = await peers[i].ReceiveFastInputAsync(cts.Token);
                Assert.Equal(MmaPacketType.Input, type);
                var relay = RelayInputPacket.ReadBody(new GmlBufferReader(body));
                Assert.Equal(1, relay.Slot);
                Assert.Equal([new InputFrameEntry(206, 0xCAFEBABEU)], relay.Frames);
            }
        }
        finally
        {
            foreach (var peer in peers)
            {
                if (peer is not null)
                {
                    await peer.DisposeAsync();
                }
            }
        }

        cts.Cancel();
        await serverTask;
    }

    [Theory]
    [InlineData("Teams", MmaConstants.OnlineModeArena, MmaConstants.SubmodeTeams, "rmWilyBoss")]
    [InlineData("Boss", MmaConstants.OnlineModeBoss, MmaConstants.SubmodeBoss, "rmMechaDragon")]
    [InlineData("Elevator", MmaConstants.OnlineModeElevator, MmaConstants.SubmodeElevator, "rmElevator")]
    [InlineData("Stage", MmaConstants.OnlineModeStage, MmaConstants.SubmodeStage, "rmBombmanStageMode0")]
    public async Task AllDedicatedModes_DeliverExpectedModeStageAndStageDifficultyOverUdp(
        string mode,
        ushort expectedOnlineMode,
        byte expectedSubmode,
        string expectedStage)
    {
        var options = new ServerOptions(IPAddress.Loopback, 0, 2, mode);
        using var host = new UdpServerHost(options, TextWriter.Null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = host.RunAsync(cts.Token);
        var serverControl = new IPEndPoint(IPAddress.Loopback, host.BoundPort);

        // Give distinct team colors (0 and 3) so Teams mode validates cleanly as well.
        await using var p1 = new TestClientPeer("A", color: 0);
        await using var p2 = new TestClientPeer("B", color: 3);

        await p1.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p1.ReadJoinSyncThroughNewPlayerDataAsync(expectedOnlineMode, expectedSubmode, cts.Token);

        await p2.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p2.ReadJoinSyncThroughNewPlayerDataAsync(expectedOnlineMode, expectedSubmode, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.NewPlayerData, null, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "ignored").WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "ignored").WritePayload, cts.Token);

        if (expectedSubmode == MmaConstants.SubmodeStage)
        {
            await p1.ReadUntilControlAsync(MmaPacketType.StageDifficulties, body =>
            {
                var diff = StageDifficultiesPacket.ReadPayload(new GmlBufferReader(body));
                Assert.Equal(0, diff.EnemyDamage);
            }, cts.Token);
        }

        var g1 = await p1.ExpectGotoLevelAsync(cts.Token);
        Assert.Equal(expectedStage, g1.Stage);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task Squads_FullLifecycleAndResendAndStageOverride_OverRealUdp()
    {
        var options = new ServerOptions(IPAddress.Loopback, 0, 2, "Squads", Stage: "rmQuickman");
        using var host = new UdpServerHost(options, TextWriter.Null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = host.RunAsync(cts.Token);
        var serverControl = new IPEndPoint(IPAddress.Loopback, host.BoundPort);

        await using var p1 = new TestClientPeer("S1", color: 0);
        await using var p2 = new TestClientPeer("S2", color: 3);

        await p1.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p1.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeSquads, MmaConstants.SubmodeSquads, cts.Token);
        await p2.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p2.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeSquads, MmaConstants.SubmodeSquads, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.NewPlayerData, null, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.SquadList, new SquadListPacket(1, 0, 1, [1, 2, 3, 4]).WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.SquadList, new SquadListPacket(2, 3, 0, [5, 6, 7, 8]).WritePayload, cts.Token);

        while (true)
        {
            bool sawPopulatedP2 = false;
            await p1.ReadUntilControlAsync(MmaPacketType.SquadList, body =>
            {
                var s = SquadListPacket.ReadPayload(new GmlBufferReader(body));
                if (s.PlayerSlot == 2 && s.Characters.Count == 4)
                {
                    Assert.Equal([5, 6, 7, 8], s.Characters);
                    sawPopulatedP2 = true;
                }
            }, cts.Token);
            if (sawPopulatedP2)
            {
                break;
            }
        }

        // Request SquadResend (27) from P1 for P2's squad.
        await p1.SendControlAsync(serverControl, MmaPacketType.SquadResend, new SquadResendPacket(1, 2).WritePayload, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.SquadList, body =>
        {
            var resent = SquadListPacket.ReadPayload(new GmlBufferReader(body));
            Assert.Equal(2, resent.PlayerSlot);
            Assert.Equal([5, 6, 7, 8], resent.Characters);
        }, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmCutman").WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmCutman").WritePayload, cts.Token);

        var gotoLevel = await p1.ExpectGotoLevelAsync(cts.Token);
        Assert.Equal("rmQuickman", gotoLevel.Stage);
        await p2.ExpectGotoLevelAsync(cts.Token);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task UdpRudp_ServerFlushesAck_DeduplicatesRetransmits_AndDropsCorruptPackets()
    {
        var options = new ServerOptions(IPAddress.Loopback, 0, 2, "Arena");
        using var host = new UdpServerHost(options, TextWriter.Null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = host.RunAsync(cts.Token);
        var serverControl = new IPEndPoint(IPAddress.Loopback, host.BoundPort);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(0, (byte)MmaPacketType.Connect).WriteTo(writer);
        new ConnectPacket(
            MmaConstants.StockVersion,
            (uint)((IPEndPoint)udp.Client.LocalEndPoint!).Port,
            0,
            false,
            "RudpProbe",
            "cloud-rudp",
            1,
            0,
            0,
            0,
            0,
            "rmWilyBoss",
            "rmMechaDragon",
            "rmBombmanStageMode0",
            1,
            false).WritePayload(writer);

        byte[] rudpConnect = GmRudpCodec.EncodeData(1u, writer.WrittenMemory.Span);
        await udp.SendAsync(rudpConnect, serverControl, cts.Token);

        // Expect RUDP DATA frame carrying NewPlayerWelcome.
        UdpReceiveResult firstReply = await udp.ReceiveAsync(cts.Token);
        Assert.True(GmRudpCodec.TryDecode(firstReply.Buffer, out GmRudpFrame welcomeFrame));
        Assert.Equal(GmRudpKind.Data, welcomeFrame.Kind);
        Assert.Equal(1u, welcomeFrame.SequenceNumber);

        // Corrupt packet (CRC mismatch) must be silently dropped without crashing or advancing player state.
        byte[] corrupt = (byte[])rudpConnect.Clone();
        corrupt[2] ^= 0xFF;
        await udp.SendAsync(corrupt, serverControl, cts.Token);

        // Retransmit exact same RUDP seq=1 Connect -> server deduplicates and immediately returns RUDP ACK for [1].
        await udp.SendAsync(rudpConnect, serverControl, cts.Token);
        while (true)
        {
            UdpReceiveResult ackRes = await udp.ReceiveAsync(cts.Token);
            Assert.True(GmRudpCodec.TryDecode(ackRes.Buffer, out GmRudpFrame frame));
            if (frame.Kind == GmRudpKind.Ack)
            {
                Assert.Contains(1u, frame.AckedSequences);
                break;
            }
        }

        Assert.Equal(1, host.Engine.ConnectedPlayerCount);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task PostMatch_OnlineUseInputRudp_IdleP1ProbedToRound2_AndServerAcksClientAcks_OverUdp()
    {
        var timings = new ServerTimingOptions(
            StartingMatchTimeout: TimeSpan.FromSeconds(5),
            MatchStallTimeout: TimeSpan.FromSeconds(3),
            TickInterval: TimeSpan.FromMilliseconds(20),
            LobbyHeartbeatInterval: TimeSpan.FromMilliseconds(160),
            FreezeRetransmitCooldown: TimeSpan.FromMilliseconds(12),
            RudpRetransmitInterval: TimeSpan.FromMilliseconds(1200),
            RudpAckFlushInterval: TimeSpan.FromMilliseconds(40),
            RudpMaxRetries: 12);

        var options = new ServerOptions(IPAddress.Loopback, 0, 2, "Arena", Buffer: 6);
        using var host = new UdpServerHost(options, TimeProvider.System, timings, TextWriter.Null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = host.RunAsync(cts.Token);
        var serverControl = new IPEndPoint(IPAddress.Loopback, host.BoundPort);

        await using var p1 = new TestClientPeer("P1");
        await using var p2 = new TestClientPeer("P2");

        await p1.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p1.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);
        await p2.ConnectAndJoinAckAsync(serverControl, false, cts.Token);
        await p2.ReadJoinSyncThroughNewPlayerDataAsync(MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.NewPlayerData, null, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload, cts.Token);
        await p1.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload, cts.Token);
        _ = await p1.ExpectGotoLevelAsync(cts.Token);
        _ = await p2.ExpectGotoLevelAsync(cts.Token);
        Assert.Equal(LobbyPhase.InMatch, host.Engine.Phase);

        // Stream RUDP Control-Port inputs (onlineUseInputRUDP = true), then P1 forfeits.
        await p1.SendControlAsync(serverControl, MmaPacketType.Input, w => new ClientInputPacket(1, 0, [new InputFrameEntry(6, 1)]).WriteBody(w), cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.Input, w => new ClientInputPacket(2, 0, [new InputFrameEntry(6, 2)]).WriteBody(w), cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.Input, null, cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.Input, null, cts.Token);

        await p1.SendControlAsync(serverControl, MmaPacketType.Forfeit, new ForfeitPacket(1).WritePayload, cts.Token);
        await p1.ReadUntilControlAsync(MmaPacketType.MatchForfeit, null, cts.Token);
        uint p1ForfeitAckSeq = p1.LastClientAckSeq!.Value;
        await p2.ReadUntilControlAsync(MmaPacketType.MatchForfeit, null, cts.Token);

        // Post-match rmOnlineLobby: P2 proactively sends GratuitousPing (23), while P1 stays silent until probed.
        await p2.SendControlAsync(serverControl, MmaPacketType.GratuitousPing, w =>
        {
            w.WriteU8(2);
            w.WriteGmlString("GRATUITOUS_PING");
        }, cts.Token);
        await p2.ReadUntilControlAsync(MmaPacketType.GratuitousPingEcho, null, cts.Token);

        // Idle P1 receives server GratuitousPing (23) after ~160ms; earlier client ACK (kind=2) must also be ACKed!
        await p1.ReadUntilControlAsync(MmaPacketType.GratuitousPing, null, cts.Token);
        Assert.Contains(p1ForfeitAckSeq, p1.AckedByServerSeqs);

        // P1 echoes 24 -> room returns to Lobby for Round 2 instead of timing out at MatchStallTimeout!
        await p1.SendControlAsync(serverControl, MmaPacketType.GratuitousPingEcho, w =>
        {
            w.WriteU8(1);
            w.WriteGmlString("GRATUITOUS_PING");
        }, cts.Token);

        while (host.Engine.Phase != LobbyPhase.Lobby)
        {
            await Task.Delay(10, cts.Token);
        }

        // Round 2 ready & launch cleanly on the live UDP sockets.
        await p1.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload, cts.Token);
        await p1.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload, cts.Token);
        await p2.SendControlAsync(serverControl, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload, cts.Token);

        GotoLevelPacket r2Goto1 = await p1.ExpectGotoLevelAsync(cts.Token);
        GotoLevelPacket r2Goto2 = await p2.ExpectGotoLevelAsync(cts.Token);
        Assert.Equal("rmWilyBoss", r2Goto1.Stage);
        Assert.Equal("rmWilyBoss", r2Goto2.Stage);
        Assert.Equal(LobbyPhase.InMatch, host.Engine.Phase);

        cts.Cancel();
        await serverTask;
    }
}
