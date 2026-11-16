using System.Net;
using Mma.Protocol;
using Mma.Server;

namespace Mma.Protocol.Tests;

public sealed class PacketCodecTests
{
    [Fact]
    public void GmlBuffer_ReadsAndWritesU64LittleEndian()
    {
        var writer = new GmlBufferWriter();
        const ulong expected = 0x1122334455667788UL;
        writer.WriteU64(expected);

        byte[] raw = writer.ToArray();
        Assert.Equal([0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11], raw);

        var reader = new GmlBufferReader(raw);
        Assert.Equal(expected, reader.ReadU64());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData("arena", "Arena", MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, "rmWilyBoss")]
    [InlineData("Teams", "Teams", MmaConstants.OnlineModeArena, MmaConstants.SubmodeTeams, "rmWilyBoss")]
    [InlineData("SQUADS", "Squads", MmaConstants.OnlineModeSquads, MmaConstants.SubmodeSquads, "rmWilyBoss")]
    [InlineData("Boss", "Boss", MmaConstants.OnlineModeBoss, MmaConstants.SubmodeBoss, "rmMechaDragon")]
    [InlineData("Elevator", "Elevator", MmaConstants.OnlineModeElevator, MmaConstants.SubmodeElevator, "rmElevator")]
    [InlineData("Stage", "Stage", MmaConstants.OnlineModeStage, MmaConstants.SubmodeStage, "rmBombmanStageMode0")]
    [InlineData("Arena8", "Arena8", MmaConstants.OnlineModeArena, MmaConstants.SubmodeArena, "rmWilyBoss")]
    public void GameModeConfig_ResolvesExpectedDefaults(
        string inputMode,
        string expectedCanonical,
        ushort expectedOnlineMode,
        byte expectedSubmode,
        string expectedDefaultStage)
    {
        var cfg = GameModeConfig.Resolve(inputMode);

        Assert.Equal(expectedCanonical, cfg.Mode);
        Assert.Equal(expectedOnlineMode, cfg.OnlineMode);
        Assert.Equal(expectedSubmode, cfg.Submode);
        Assert.Equal(expectedDefaultStage, cfg.DefaultStage);
        Assert.Equal(MmaConstants.DefaultStockCount, cfg.StockCount);
    }

    [Fact]
    public void ServerOptions_ResolvesStageBufferAndProtocolVariant()
    {
        var stock4p = ServerOptions.Parse(["--mode", "Boss", "--max-players", "4", "--buffer", "9"]);
        Assert.False(stock4p.UsesArena8Protocol);
        Assert.Equal(MmaConstants.StockVersion, stock4p.ExpectedVersion);
        Assert.Equal("rmMechaDragon", stock4p.ResolvedStage);
        Assert.Equal(9, stock4p.Buffer);

        var env = new Dictionary<string, string?>
        {
            ["MMA_STAGE"] = "rmCutmanStageMode0",
            ["MMA_BUFFER"] = "4",
            ["MMA_MODE"] = "Stage",
            ["MMA_MAX_PLAYERS"] = "4"
        };
        var overridden = ServerOptions.Parse(
            ["--stage", "rmElecmanStageMode1"],
            key => env.GetValueOrDefault(key));
        Assert.Equal("rmElecmanStageMode1", overridden.ResolvedStage);
        Assert.Equal(4, overridden.Buffer);
        Assert.False(overridden.UsesArena8Protocol);

        var arena8ByPlayers = new ServerOptions(IPAddress.Loopback, 6520, 6, "Arena");
        Assert.True(arena8ByPlayers.UsesArena8Protocol);
        Assert.Equal(MmaConstants.Arena8Version, arena8ByPlayers.ExpectedVersion);

        var arena8ByMode = new ServerOptions(IPAddress.Loopback, 6528, 4, "Arena8");
        Assert.True(arena8ByMode.UsesArena8Protocol);
        Assert.Equal(MmaConstants.Arena8Version, arena8ByMode.ExpectedVersion);

        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--port", "65535"]));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--buffer", "13"]));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--mode", "UnknownMode"]));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--stage", "   "]));
    }

    [Fact]
    public void WelcomePacket_Stock4PIncludesTag_Arena8OmitsTag()
    {
        var p1 = new PlayerSummary("Mega", 1, 0, 0x11223344, 0x55667788, 2, "rmWilyBoss", true, 1, false);
        var stock = new NewPlayerWelcomePacket(777, 2, 0, [p1], Arena8Format: false);
        var arena8 = new NewPlayerWelcomePacket(777, 2, 0, [p1], Arena8Format: true);

        var stockWriter = new GmlBufferWriter();
        new MmaPacketHeader(777, (byte)MmaPacketType.NewPlayerWelcome).WriteTo(stockWriter);
        stock.WritePayload(stockWriter);
        byte[] stockBytes = stockWriter.ToArray();

        var arena8Writer = new GmlBufferWriter();
        new MmaPacketHeader(777, (byte)MmaPacketType.NewPlayerWelcome).WriteTo(arena8Writer);
        arena8.WritePayload(arena8Writer);
        byte[] arena8Bytes = arena8Writer.ToArray();

        // Stock appends NUL-terminated "NEW PLAYER JOIN" (16 bytes); Arena8 omits it.
        Assert.Equal(16, stockBytes.Length - arena8Bytes.Length);

        var stockReader = new GmlBufferReader(stockBytes);
        var hdr1 = MmaPacketHeader.ReadFrom(stockReader);
        Assert.Equal(MmaPacketType.NewPlayerWelcome, hdr1.KnownType);
        var decodedStock = NewPlayerWelcomePacket.ReadPayload(stockReader, arena8Format: false);
        Assert.Equal(777, decodedStock.AssignedSessionId);
        Assert.Equal(2, decodedStock.PlayerSlot);
        Assert.Single(decodedStock.ExistingPlayers);
        Assert.Equal("Mega", decodedStock.ExistingPlayers[0].Name);
        Assert.Equal(0, stockReader.Remaining);

        var arena8Reader = new GmlBufferReader(arena8Bytes);
        _ = MmaPacketHeader.ReadFrom(arena8Reader);
        var decodedArena8 = NewPlayerWelcomePacket.ReadPayload(arena8Reader, arena8Format: true);
        Assert.Equal(777, decodedArena8.AssignedSessionId);
        Assert.Single(decodedArena8.ExistingPlayers);
        Assert.Equal(0, arena8Reader.Remaining);
    }

    [Fact]
    public void SquadAndGotoLevelAndInputPackets_RoundTripExactWireFormat()
    {
        // Squad packet: empty vs populated (no trailing tag string).
        var emptySquad = new SquadListPacket(2, 4, 1, []);
        var squadWriter = new GmlBufferWriter();
        emptySquad.WritePayload(squadWriter);
        byte[] emptyBytes = squadWriter.ToArray();
        Assert.Equal(6, emptyBytes.Length); // count(1) + slot(1) + color(1) + slide(1) + 9999(u16)
        var decodedEmpty = SquadListPacket.ReadPayload(new GmlBufferReader(emptyBytes));
        Assert.Empty(decodedEmpty.Characters);
        Assert.Equal(2, decodedEmpty.PlayerSlot);

        var fullSquad = new SquadListPacket(1, 3, 0, [1, 2, 17, 25]);
        var fullWriter = new GmlBufferWriter();
        fullSquad.WritePayload(fullWriter);
        var decodedFull = SquadListPacket.ReadPayload(new GmlBufferReader(fullWriter.WrittenMemory));
        Assert.Equal([1, 2, 17, 25], decodedFull.Characters);

        // GotoLevel with 64-bit randomSeed.
        var gotoLevel = new GotoLevelPacket("rmWilyBoss", 0xCAFEBABE12345678UL, 6, true, true, 4, false);
        var gotoWriter = new GmlBufferWriter();
        new MmaPacketHeader(99, (byte)MmaPacketType.GotoLevel).WriteTo(gotoWriter);
        gotoLevel.WritePayload(gotoWriter);
        var gotoReader = new GmlBufferReader(gotoWriter.WrittenMemory);
        _ = MmaPacketHeader.ReadFrom(gotoReader);
        var decodedGoto = GotoLevelPacket.ReadPayload(gotoReader);
        Assert.Equal("rmWilyBoss", decodedGoto.Stage);
        Assert.Equal(0xCAFEBABE12345678UL, decodedGoto.RandomSeed);
        Assert.Equal(6, decodedGoto.Buffer);
        Assert.Equal(0, gotoReader.Remaining);

        // ClientInput vs RelayInput asymmetry (ClientMatchFrame present C->S, omitted S->C).
        var clientIn = new ClientInputPacket(2, 120, [new InputFrameEntry(124, 0x80000000), new InputFrameEntry(125, 0x08000000)]);
        var fastInWriter = new GmlBufferWriter();
        clientIn.WriteToFastInput(fastInWriter);
        var fastInReader = new GmlBufferReader(fastInWriter.WrittenMemory);
        Assert.Equal((byte)MmaPacketType.Input, fastInReader.ReadU8());
        var parsedClientIn = ClientInputPacket.ReadBody(fastInReader);
        Assert.Equal(120u, parsedClientIn.ClientMatchFrame);
        Assert.Equal(2, parsedClientIn.Frames.Count);

        var relayOut = new RelayInputPacket(2, parsedClientIn.Frames);
        var controlOutWriter = new GmlBufferWriter();
        relayOut.WriteToControl(controlOutWriter, 555);
        // Header(3) + Count(1) + Slot(1) + 2 * 8 bytes = 21 bytes (4 bytes shorter than ClientInput!).
        Assert.Equal(21, controlOutWriter.WrittenCount);
        var controlOutReader = new GmlBufferReader(controlOutWriter.WrittenMemory);
        var hdr = MmaPacketHeader.ReadFrom(controlOutReader);
        Assert.Equal(555, hdr.SessionId);
        Assert.Equal(MmaPacketType.Input, hdr.KnownType);
        var parsedRelay = RelayInputPacket.ReadBody(controlOutReader);
        Assert.Equal(parsedClientIn.Frames, parsedRelay.Frames);
        Assert.Equal(0, controlOutReader.Remaining);
    }

    [Fact]
    public void DeadlinePackets_ProposalAndBatch_RoundTripAndValidateLengths()
    {
        Assert.Equal("5.0.0.4890-arena8-deadline", MmaConstants.Arena8Version);

        var proposal = new DeadlineProposalPacket(
            Token: 0xABCDEF12u,
            Phase: 2,
            AckFrame: 42,
            Proposals: [new InputFrameEntry(45, 0x00000015u), new InputFrameEntry(46, 0x0000002Au)]);
        var propWriter = new GmlBufferWriter();
        proposal.WriteTo(propWriter, sessionId: 9123);
        byte[] propBytes = propWriter.ToArray();
        Assert.Equal(14 + (2 * 8), propBytes.Length);
        Assert.True(DeadlineProposalPacket.TryReadPayload(propBytes, out DeadlineProposalPacket? decodedProp));
        Assert.NotNull(decodedProp);
        Assert.Equal(0xABCDEF12u, decodedProp.Token);
        Assert.Equal((ushort)2, decodedProp.Phase);
        Assert.Equal(42u, decodedProp.AckFrame);
        Assert.Equal(proposal.Proposals, decodedProp.Proposals);

        // Truncated or oversized count must be rejected safely.
        Assert.False(DeadlineProposalPacket.TryReadPayload(propBytes.AsSpan(0, propBytes.Length - 1), out _));
        byte[] badCountProp = (byte[])propBytes.Clone();
        badCountProp[13] = 17;
        Assert.False(DeadlineProposalPacket.TryReadPayload(badCountProp, out _));

        var batch = new DeadlineBatchPacket(
            Token: 0xABCDEF12u,
            Phase: 2,
            HighestCommitted: 48,
            PlayerCount: 5,
            Frames:
            [
                new DeadlineFrameSnapshot(47, [1, 2, 3, 4, 5]),
                new DeadlineFrameSnapshot(48, [10, 20, 30, 40, 50])
            ]);
        var batchWriter = new GmlBufferWriter();
        batch.WriteTo(batchWriter, sessionId: 9123);
        byte[] batchBytes = batchWriter.ToArray();
        Assert.Equal(14 + (2 * (4 + (5 * 4))), batchBytes.Length);

        Assert.True(DeadlineBatchPacket.TryReadPayload(batchBytes, playerCount: 5, out DeadlineBatchPacket? decodedBatch));
        Assert.NotNull(decodedBatch);
        Assert.Equal(48u, decodedBatch.HighestCommitted);
        Assert.Equal(2, decodedBatch.Frames.Count);
        Assert.Equal([10u, 20u, 30u, 40u, 50u], decodedBatch.Frames[1].PlayerMasks);

        Assert.False(DeadlineBatchPacket.TryReadPayload(batchBytes, playerCount: 4, out _));
        Assert.True(DeadlineBatchPacket.TryReadBody(batchBytes.AsSpan(MmaPacketHeader.ByteSize), 5, out DeadlineBatchPacket? decodedBody));
        Assert.NotNull(decodedBody);
        Assert.Equal(decodedBatch.HighestCommitted, decodedBody.HighestCommitted);
    }
}
