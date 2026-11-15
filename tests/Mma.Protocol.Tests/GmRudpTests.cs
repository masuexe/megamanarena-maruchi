using System.Net;
using Mma.Protocol;
using Mma.Server;

namespace Mma.Protocol.Tests;

public sealed class GmRudpTests
{
    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = startUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private static byte[] BuildGmlControl(ushort sessionId, MmaPacketType type, Action<GmlBufferWriter>? writeBody = null)
    {
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(sessionId, (byte)type).WriteTo(writer);
        writeBody?.Invoke(writer);
        return writer.ToArray();
    }

    private static ConnectPacket MakeConnect(string name, int udpPort) => new(
        Version: MmaConstants.StockVersion,
        ClientPort: 41000,
        ClientUdpPort: (uint)udpPort,
        IsObserver: false,
        Name: name,
        CloudId: "cloud-" + name,
        Character: 1,
        Color: 0,
        CustomPrimaryColor: 0,
        CustomSecondaryColor: 0,
        Skin: 0,
        ArenaStage: "rmWilyBoss",
        BossStage: "rmMechaDragon",
        StageModeStage: "rmBombmanStageMode0",
        SlideDownJump: 1,
        CheatEnabled: false);

    [Fact]
    public void Codec_MatchesObservedGameMakerWireSampleAck()
    {
        // Real GameMaker client capture: ~CRC32=0xF74D145D, kind=2 (ACK), seq=12, acked=[1, 2].
        byte[] captured =
        [
            0x5D, 0x14, 0x4D, 0xF7,
            0x02, 0x00, 0x00, 0x00,
            0x0C, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00
        ];

        byte[] encoded = GmRudpCodec.EncodeAck(12u, [1u, 2u]);
        Assert.Equal(captured, encoded);

        Assert.True(GmRudpCodec.TryDecode(captured, out GmRudpFrame frame));
        Assert.Equal(GmRudpKind.Ack, frame.Kind);
        Assert.Equal(12u, frame.SequenceNumber);
        Assert.Equal([1u, 2u], frame.AckedSequences);
        Assert.True(frame.Payload.IsEmpty);
    }

    [Fact]
    public void Codec_DataRoundTripAndValidation_RejectsBitFlipsAndMalformedFrames()
    {
        byte[] innerPayload = BuildGmlControl(7087, MmaPacketType.Ping, w => w.WriteBool(false));
        byte[] datagram = GmRudpCodec.EncodeData(42u, innerPayload);

        Assert.Equal(GmRudpCodec.HeaderSize + innerPayload.Length, datagram.Length);
        Assert.True(GmRudpCodec.TryDecode(datagram, out GmRudpFrame decoded));
        Assert.Equal(GmRudpKind.Data, decoded.Kind);
        Assert.Equal(42u, decoded.SequenceNumber);
        Assert.Equal(innerPayload, decoded.Payload.ToArray());

        // Single-bit flip in any byte of checksum, header, or payload must fail CRC verification.
        for (int i = 0; i < datagram.Length; i++)
        {
            byte[] corrupted = (byte[])datagram.Clone();
            corrupted[i] ^= 0x01;
            Assert.False(GmRudpCodec.TryDecode(corrupted, out _));
        }

        // Sequence 0 and invalid ACK counts throw / fail decode.
        Assert.Throws<ArgumentOutOfRangeException>(() => GmRudpCodec.EncodeData(0u, innerPayload));
        Assert.Throws<ArgumentOutOfRangeException>(() => GmRudpCodec.EncodeAck(0u, [1u]));
        Assert.Throws<ArgumentOutOfRangeException>(() => GmRudpCodec.EncodeAck(1u, Array.Empty<uint>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => GmRudpCodec.EncodeAck(1u, new uint[GmRudpCodec.MaxAckEntriesPerPacket + 1]));

        byte[] emptyData = new byte[GmRudpCodec.HeaderSize];
        emptyData[4] = 1;
        emptyData[8] = 1;
        uint emptyDataCrc = GmRudpCodec.ComputeChecksum(emptyData.AsSpan(4));
        BitConverter.GetBytes(emptyDataCrc).CopyTo(emptyData, 0);
        Assert.False(GmRudpCodec.TryDecode(emptyData, out _));
    }

    [Fact]
    public void ChannelHost_DeduplicatesInboundSeq_BatchesAcks_AndRetransmitsUntilAcked()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var timings = new ServerTimingOptions(
            StartingMatchTimeout: TimeSpan.FromSeconds(10),
            MatchStallTimeout: TimeSpan.FromSeconds(15),
            TickInterval: TimeSpan.FromMilliseconds(50),
            LobbyHeartbeatInterval: TimeSpan.FromMinutes(10),
            FreezeRetransmitCooldown: TimeSpan.FromMilliseconds(12),
            RudpRetransmitInterval: TimeSpan.FromMilliseconds(1200),
            RudpAckFlushInterval: TimeSpan.FromMilliseconds(100),
            RudpMaxRetries: 3);
        var options = new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena");
        var engine = new MmaServerEngine(options, clock, timings, TextWriter.Null);
        var host = new GmRudpChannelHost(engine, clock, timings);

        var p1Ep = new IPEndPoint(IPAddress.Loopback, 50001);
        byte[] connectGml = BuildGmlControl(0, MmaPacketType.Connect, MakeConnect("Mega", 50002).WritePayload);
        byte[] connectFrame1 = GmRudpCodec.EncodeData(1u, connectGml);

        // First Connect(seq=1): wrapped NewPlayerWelcome sent with server RUDP seq=1.
        var out1 = host.ProcessControlDatagram(p1Ep, connectFrame1);
        var welcomeDgram = Assert.Single(out1);
        Assert.Equal(UdpTransportSocket.Control, welcomeDgram.Socket);
        Assert.True(GmRudpCodec.TryDecode(welcomeDgram.Payload, out GmRudpFrame welcomeFrame));
        Assert.Equal(GmRudpKind.Data, welcomeFrame.Kind);
        Assert.Equal(1u, welcomeFrame.SequenceNumber);

        var reader = new GmlBufferReader(welcomeFrame.Payload);
        Assert.Equal(MmaPacketType.NewPlayerWelcome, MmaPacketHeader.ReadFrom(reader).KnownType);
        _ = NewPlayerWelcomePacket.ReadPayload(reader, arena8Format: false);
        Assert.Equal(0, reader.Remaining);

        // Duplicate retransmit of seq=1: must NOT re-run Connect, MUST immediately send ACK=[1].
        var dupOut = host.ProcessControlDatagram(p1Ep, connectFrame1);
        var dupAckDgram = Assert.Single(dupOut);
        Assert.True(GmRudpCodec.TryDecode(dupAckDgram.Payload, out GmRudpFrame dupAckFrame));
        Assert.Equal(GmRudpKind.Ack, dupAckFrame.Kind);
        Assert.Equal([1u], dupAckFrame.AckedSequences);

        // Corrupt packet from known RUDP peer is dropped cleanly.
        byte[] badPacket = (byte[])connectFrame1.Clone();
        badPacket[0] ^= 0x80;
        Assert.Empty(host.ProcessControlDatagram(p1Ep, badPacket));

        // Before 1200ms elapses, only ACK flush occurs at 100ms (for non-dup seq if queued) and NO retransmit.
        byte[] joinAckFrame2 = GmRudpCodec.EncodeData(2u, BuildGmlControl(engine.SessionId, MmaPacketType.NewPlayerJoinAck, new NewPlayerJoinAckPacket(1, false).WritePayload));
        var joinOut = host.ProcessControlDatagram(p1Ep, joinAckFrame2);
        Assert.True(joinOut.Count >= 9);
        Assert.All(joinOut, d =>
        {
            Assert.True(GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f));
            Assert.Equal(GmRudpKind.Data, f.Kind);
        });

        // Advance 100ms -> Tick flushes ACK containing [2].
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var tickAckOut = host.Tick();
        Assert.Contains(tickAckOut, d =>
            GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) &&
            f.Kind == GmRudpKind.Ack &&
            f.AckedSequences.Contains(2u));

        // Advance another 1150ms -> total >= 1200ms since welcome & join sync, server retransmits unacked DATA frames.
        clock.Advance(TimeSpan.FromMilliseconds(1150));
        var retransmitOut = host.Tick();
        Assert.Contains(retransmitOut, d =>
            GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) &&
            f.Kind == GmRudpKind.Data &&
            f.SequenceNumber == 1u);

        // Client ACKs server seq=1; subsequent retransmit window must not resend seq=1.
        byte[] clientAck = GmRudpCodec.EncodeAck(3u, [1u]);
        Assert.Empty(host.ProcessControlDatagram(p1Ep, clientAck));
        clock.Advance(TimeSpan.FromMilliseconds(1250));
        var afterAckRetransmit = host.Tick();
        Assert.DoesNotContain(afterAckRetransmit, d =>
            GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) &&
            f.Kind == GmRudpKind.Data &&
            f.SequenceNumber == 1u);

        // Advance past remaining RudpMaxRetries (3); exhausted frames are abandoned without leaking/looping forever.
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1250));
            _ = host.Tick();
        }

        clock.Advance(TimeSpan.FromMilliseconds(1250));
        var afterExhaustion = host.Tick();
        Assert.DoesNotContain(afterExhaustion, d =>
            GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) && f.Kind == GmRudpKind.Data);
    }

    [Fact]
    public void ChannelHost_KeepsFastInputUnframed_AndAllowsFreshReconnectOnSamePortAfterReset()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var timings = ServerTimingOptions.Default;
        var engine = new MmaServerEngine(new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena"), clock, timings, TextWriter.Null);
        var host = new GmRudpChannelHost(engine, clock, timings);

        var p1Control = new IPEndPoint(IPAddress.Loopback, 51001);
        var p1FastInput = new IPEndPoint(IPAddress.Loopback, 51002);
        var p2Control = new IPEndPoint(IPAddress.Loopback, 52001);
        var p2FastInput = new IPEndPoint(IPAddress.Loopback, 52002);

        // Connect and ready both players via RUDP.
        byte[] p1Connect = GmRudpCodec.EncodeData(1u, BuildGmlControl(0, MmaPacketType.Connect, MakeConnect("P1", 51002).WritePayload));
        byte[] p2Connect = GmRudpCodec.EncodeData(1u, BuildGmlControl(0, MmaPacketType.Connect, MakeConnect("P2", 52002).WritePayload));
        _ = host.ProcessControlDatagram(p1Control, p1Connect);
        _ = host.ProcessControlDatagram(p2Control, p2Connect);
        _ = host.ProcessControlDatagram(p1Control, GmRudpCodec.EncodeData(2u, BuildGmlControl(engine.SessionId, MmaPacketType.NewPlayerJoinAck, new NewPlayerJoinAckPacket(1, false).WritePayload)));
        _ = host.ProcessControlDatagram(p2Control, GmRudpCodec.EncodeData(2u, BuildGmlControl(engine.SessionId, MmaPacketType.NewPlayerJoinAck, new NewPlayerJoinAckPacket(2, false).WritePayload)));
        _ = host.ProcessControlDatagram(p1Control, GmRudpCodec.EncodeData(3u, BuildGmlControl(engine.SessionId, MmaPacketType.PlayerReady, new ClientReadyPacket(1).WritePayload)));
        _ = host.ProcessControlDatagram(p2Control, GmRudpCodec.EncodeData(3u, BuildGmlControl(engine.SessionId, MmaPacketType.PlayerReady, new ClientReadyPacket(2).WritePayload)));
        _ = host.ProcessControlDatagram(p1Control, GmRudpCodec.EncodeData(4u, BuildGmlControl(engine.SessionId, MmaPacketType.GotoLevel, new MatchReadyPacket(1, "rmWilyBoss").WritePayload)));
        _ = host.ProcessControlDatagram(p2Control, GmRudpCodec.EncodeData(4u, BuildGmlControl(engine.SessionId, MmaPacketType.GotoLevel, new MatchReadyPacket(2, "rmWilyBoss").WritePayload)));

        Assert.Equal(LobbyPhase.InMatch, engine.Phase);

        // Fast Port+1 input from P1 -> must relay on Input socket as UNFRAMED packet starting with dataType=0.
        var inWriter = new GmlBufferWriter();
        new ClientInputPacket(1, 10, [new InputFrameEntry(11u, 0x1234u)]).WriteToFastInput(inWriter);
        var fastRelay = host.ProcessInputDatagram(p1FastInput, inWriter.WrittenMemory);
        var relayDgram = Assert.Single(fastRelay);
        Assert.Equal(UdpTransportSocket.Input, relayDgram.Socket);
        Assert.Equal(p2FastInput, relayDgram.Target);
        Assert.Equal((byte)MmaPacketType.Input, relayDgram.Payload[0]);
        Assert.False(GmRudpCodec.TryDecode(relayDgram.Payload, out _));

        // P1 disconnects cleanly; room resets.
        _ = host.ProcessControlDatagram(p1Control, GmRudpCodec.EncodeData(5u, BuildGmlControl(engine.SessionId, MmaPacketType.Disconnect, w => w.WriteU8(1))));
        Assert.Equal(0, engine.ConnectedPlayerCount);

        // New client restarts on same P1 IP:Port starting at RUDP seq=1 -> must reset inbound seq window and accept Connect.
        var reconnectOut = host.ProcessControlDatagram(p1Control, p1Connect);
        var welcomeAgain = Assert.Single(reconnectOut);
        Assert.True(GmRudpCodec.TryDecode(welcomeAgain.Payload, out GmRudpFrame f));
        Assert.Equal(GmRudpKind.Data, f.Kind);
        var r = new GmlBufferReader(f.Payload);
        Assert.Equal(MmaPacketType.NewPlayerWelcome, MmaPacketHeader.ReadFrom(r).KnownType);
        Assert.Equal(1, engine.ConnectedPlayerCount);
    }

    [Fact]
    public void ChannelHost_RawFallbackAcceptsShortDiagnosticPacket()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var engine = new MmaServerEngine(new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena"), clock, ServerTimingOptions.Default, TextWriter.Null);
        var host = new GmRudpChannelHost(engine, clock, ServerTimingOptions.Default);

        // 4-byte unframed packet (same shape as UdpServerHostTests diagnostic check)
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(7087, (byte)MmaPacketType.Connect).WriteTo(writer);
        writer.WriteU8(1);

        var outDgrams = host.ProcessControlDatagram(new IPEndPoint(IPAddress.Loopback, 55555), writer.WrittenMemory);
        Assert.Empty(outDgrams);
        Assert.False(host.IsKnownRudpPeer(new IPEndPoint(IPAddress.Loopback, 55555)));
    }

    [Fact]
    public void ChannelHost_FlushesMax62AckBatch_AndPrunesClosingPeerAfterLinger()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var timings = ServerTimingOptions.Default;
        var engine = new MmaServerEngine(new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena"), clock, timings, TextWriter.Null);
        var host = new GmRudpChannelHost(engine, clock, timings);

        // 1. DisallowConnect sets linger timer, then prunes after ClosingLingerDuration (3s)
        var badEp = new IPEndPoint(IPAddress.Loopback, 53001);
        ConnectPacket badVersion = MakeConnect("OldClient", 53002) with { Version = "0.0.0" };
        byte[] badConnect = GmRudpCodec.EncodeData(1u, BuildGmlControl(0, MmaPacketType.Connect, badVersion.WritePayload));
        var disallowOut = host.ProcessControlDatagram(badEp, badConnect);
        Assert.Single(disallowOut);
        Assert.True(host.IsKnownRudpPeer(badEp));

        clock.Advance(TimeSpan.FromSeconds(4));
        _ = host.Tick();
        Assert.False(host.IsKnownRudpPeer(badEp));

        // 2. Sending 65 unique inbound RUDP DATA frames triggers immediate 62-entry ACK batch + remainder on tick.
        var goodEp = new IPEndPoint(IPAddress.Loopback, 54001);
        _ = host.ProcessControlDatagram(goodEp, GmRudpCodec.EncodeData(1u, BuildGmlControl(0, MmaPacketType.Connect, MakeConnect("AckFlood", 54002).WritePayload)));

        int ack62Count = 0;
        for (uint seq = 2; seq <= 65; seq++)
        {
            byte[] ping = GmRudpCodec.EncodeData(seq, BuildGmlControl(engine.SessionId, MmaPacketType.Ping, w => w.WriteBool(false)));
            var reply = host.ProcessControlDatagram(goodEp, ping);
            foreach (OutboundDatagram d in reply)
            {
                if (GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) && f.Kind == GmRudpKind.Ack)
                {
                    Assert.Equal(GmRudpCodec.MaxAckEntriesPerPacket, f.AckedSequences.Count);
                    ack62Count++;
                }
            }
        }

        Assert.Equal(1, ack62Count);

        clock.Advance(TimeSpan.FromMilliseconds(150));
        var remainderTick = host.Tick();
        Assert.Contains(remainderTick, d =>
            GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) &&
            f.Kind == GmRudpKind.Ack &&
            f.AckedSequences.Count == 3);
    }

    [Fact]
    public void Codec_DecodesLargeAckFramesAbove62Entries()
    {
        // Real GM clients can batch 64..68+ ACK entries in a single kind=2 packet (e.g. 68 * 4 + 12 = 284B).
        const int ackCount = 68;
        byte[] wire = new byte[GmRudpCodec.HeaderSize + (ackCount * sizeof(uint))];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(4, 4), (uint)GmRudpKind.Ack);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(8, 4), 77u);
        for (int i = 0; i < ackCount; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                wire.AsSpan(GmRudpCodec.HeaderSize + (i * sizeof(uint)), 4),
                (uint)(100 + i));
        }

        uint crc = GmRudpCodec.ComputeChecksum(wire.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(0, 4), crc);

        Assert.True(GmRudpCodec.TryDecode(wire, out GmRudpFrame frame));
        Assert.Equal(GmRudpKind.Ack, frame.Kind);
        Assert.Equal(77u, frame.SequenceNumber);
        Assert.Equal(ackCount, frame.AckedSequences.Count);
        Assert.Equal(167u, frame.AckedSequences[^1]);
    }

    [Fact]
    public void ChannelHost_AcknowledgesInboundAckFrames_FlushesDuplicateAcks_AndRetainsClosingPeerUntilAckFlushed()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var timings = new ServerTimingOptions(
            StartingMatchTimeout: TimeSpan.FromSeconds(10),
            MatchStallTimeout: TimeSpan.FromSeconds(15),
            TickInterval: TimeSpan.FromMilliseconds(50),
            LobbyHeartbeatInterval: TimeSpan.FromMinutes(10),
            FreezeRetransmitCooldown: TimeSpan.FromMilliseconds(12),
            RudpRetransmitInterval: TimeSpan.FromMilliseconds(1200),
            RudpAckFlushInterval: TimeSpan.FromMilliseconds(100),
            RudpMaxRetries: 3);
        var engine = new MmaServerEngine(new ServerOptions(IPAddress.Loopback, 6520, 2, "Arena"), clock, timings, TextWriter.Null);
        var host = new GmRudpChannelHost(engine, clock, timings);

        var p1Ep = new IPEndPoint(IPAddress.Loopback, 55001);
        _ = host.ProcessControlDatagram(p1Ep, GmRudpCodec.EncodeData(1u, BuildGmlControl(0, MmaPacketType.Connect, MakeConnect("AckPeer", 55002).WritePayload)));

        // First inbound kind=2 ACK (seq=2) queues ACK=2 cleanly.
        byte[] ack2 = GmRudpCodec.EncodeAck(2u, [1u]);
        Assert.Empty(host.ProcessControlDatagram(p1Ep, ack2));

        // Retransmitted kind=2 ACK (seq=2) flushes ACK=[1, 2] immediately so GM client stops 3s retransmit.
        var dupReply = host.ProcessControlDatagram(p1Ep, ack2);
        var dupAck = Assert.Single(dupReply);
        Assert.True(GmRudpCodec.TryDecode(dupAck.Payload, out GmRudpFrame dupAckFrame));
        Assert.Equal(GmRudpKind.Ack, dupAckFrame.Kind);
        Assert.Equal([1u, 2u], dupAckFrame.AckedSequences);

        // Fresh inbound kind=2 ACK (seq=3) is acknowledged on the 100ms Tick flush.
        Assert.Empty(host.ProcessControlDatagram(p1Ep, GmRudpCodec.EncodeAck(3u, [1u])));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var tickAckOut = host.Tick();
        Assert.Contains(tickAckOut, d =>
            GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) &&
            f.Kind == GmRudpKind.Ack &&
            f.AckedSequences.Contains(3u));

        // Closing peer must stay alive after ACKing DisallowConnect until our ACK for that client ACK flushes.
        var badEp = new IPEndPoint(IPAddress.Loopback, 55003);
        ConnectPacket badVersion = MakeConnect("OldPeer", 55004) with { Version = "0.0.0" };
        var disallowDgram = Assert.Single(host.ProcessControlDatagram(
            badEp,
            GmRudpCodec.EncodeData(1u, BuildGmlControl(0, MmaPacketType.Connect, badVersion.WritePayload))));
        Assert.True(GmRudpCodec.TryDecode(disallowDgram.Payload, out GmRudpFrame disallowFrame));

        Assert.Empty(host.ProcessControlDatagram(badEp, GmRudpCodec.EncodeAck(2u, [disallowFrame.SequenceNumber])));
        Assert.True(host.IsKnownRudpPeer(badEp));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var badFlush = host.Tick();
        Assert.Contains(badFlush, d =>
            d.Target.Equals(badEp) &&
            GmRudpCodec.TryDecode(d.Payload, out GmRudpFrame f) &&
            f.Kind == GmRudpKind.Ack &&
            f.AckedSequences.Contains(2u));
        Assert.False(host.IsKnownRudpPeer(badEp));
    }
}
