namespace Mma.Protocol;

/// <summary>
/// Compact player profile carried in welcome and join broadcasts.
/// </summary>
public sealed record PlayerSummary(
    string Name,
    short Character,
    byte Color,
    uint CustomPrimaryColor,
    uint CustomSecondaryColor,
    byte Skin,
    string Stage,
    bool Ready,
    byte SlideDownJump,
    bool CheatEnabled)
{
    public void WriteTo(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteGmlString(Name);
        writer.WriteS16(Character);
        writer.WriteU8(Color);
        writer.WriteU32(CustomPrimaryColor);
        writer.WriteU32(CustomSecondaryColor);
        writer.WriteU8(Skin);
        writer.WriteGmlString(Stage);
        writer.WriteBool(Ready);
        writer.WriteU8(SlideDownJump);
        writer.WriteBool(CheatEnabled);
    }

    public static PlayerSummary ReadFrom(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new PlayerSummary(
            reader.ReadGmlString(),
            reader.ReadS16(),
            reader.ReadU8(),
            reader.ReadU32(),
            reader.ReadU32(),
            reader.ReadU8(),
            reader.ReadGmlString(),
            reader.ReadBool(),
            reader.ReadU8(),
            reader.ReadBool());
    }
}

/// <summary>
/// Client handshake packet (DataType = 1).
/// </summary>
public sealed record ConnectPacket(
    string Version,
    uint ClientPort,
    uint ClientUdpPort,
    bool IsObserver,
    string Name,
    string CloudId,
    short Character,
    byte Color,
    uint CustomPrimaryColor,
    uint CustomSecondaryColor,
    byte Skin,
    string ArenaStage,
    string BossStage,
    string StageModeStage,
    byte SlideDownJump,
    bool CheatEnabled,
    string? PacketTag = "CONNECT")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteGmlString(Version);
        writer.WriteU32(ClientPort);
        writer.WriteU32(ClientUdpPort);
        writer.WriteBool(IsObserver);
        writer.WriteGmlString(Name);
        writer.WriteGmlString(CloudId);
        writer.WriteS16(Character);
        writer.WriteU8(Color);
        writer.WriteU32(CustomPrimaryColor);
        writer.WriteU32(CustomSecondaryColor);
        writer.WriteU8(Skin);
        writer.WriteGmlString(ArenaStage);
        writer.WriteGmlString(BossStage);
        writer.WriteGmlString(StageModeStage);
        writer.WriteU8(SlideDownJump);
        writer.WriteBool(CheatEnabled);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static ConnectPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ConnectPacket(
            reader.ReadGmlString(),
            reader.ReadU32(),
            reader.ReadU32(),
            reader.ReadBool(),
            reader.ReadGmlString(),
            reader.ReadGmlString(),
            reader.ReadS16(),
            reader.ReadU8(),
            reader.ReadU32(),
            reader.ReadU32(),
            reader.ReadU8(),
            reader.ReadGmlString(),
            reader.ReadGmlString(),
            reader.ReadGmlString(),
            reader.ReadU8(),
            reader.ReadBool(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Server welcome packet (DataType = 4) assigning session ID, slot, and prior roster.
/// Stock 4P builds include a trailing "NEW PLAYER JOIN" tag; Arena8 omits it.
/// </summary>
public sealed record NewPlayerWelcomePacket(
    ushort AssignedSessionId,
    byte PlayerSlot,
    byte ObserverCount,
    IReadOnlyList<PlayerSummary> ExistingPlayers,
    bool Arena8Format)
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU16(AssignedSessionId);
        writer.WriteU8(PlayerSlot);
        writer.WriteU8(ObserverCount);
        foreach (PlayerSummary entry in ExistingPlayers)
        {
            entry.WriteTo(writer);
        }

        if (!Arena8Format)
        {
            writer.WriteGmlString("NEW PLAYER JOIN");
        }
    }

    public static NewPlayerWelcomePacket ReadPayload(GmlBufferReader reader, bool arena8Format)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ushort assignedSessionId = reader.ReadU16();
        byte playerSlot = reader.ReadU8();
        byte observerCount = reader.ReadU8();
        int existingCount = Math.Max(0, playerSlot - 1);
        var entries = new PlayerSummary[existingCount];
        for (int i = 0; i < existingCount; i++)
        {
            entries[i] = PlayerSummary.ReadFrom(reader);
        }

        if (!arena8Format)
        {
            _ = reader.TryReadOptionalGmlString();
        }

        return new NewPlayerWelcomePacket(assignedSessionId, playerSlot, observerCount, entries, arena8Format);
    }
}

/// <summary>
/// Client acknowledgment of welcome packet (DataType = 22).
/// </summary>
public sealed record NewPlayerJoinAckPacket(byte PlayerSlot, bool IsObserver, string? PacketTag = "NEW_PLAYER_JOIN_ACK")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        writer.WriteBool(IsObserver);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static NewPlayerJoinAckPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new NewPlayerJoinAckPacket(reader.ReadU8(), reader.ReadBool(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Server confirmation of online mode sent 6x in join-ack (DataType = 2).
/// </summary>
public sealed record ConnectAckPacket(ushort OnlineMode, string? PacketTag = "CONNECT_ACK")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU16(OnlineMode);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static ConnectAckPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ConnectAckPacket(reader.ReadU16(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Match rules snapshot packet (DataType = 12).
/// </summary>
public sealed record RulesUpdatePacket(
    bool Hazards,
    bool PitSpikeHazards,
    byte StockCount,
    byte EpOption,
    bool TeamAttack,
    ushort StageSelection,
    string? PacketTag = "RULES UPDATE DURING JOIN ACK")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBool(Hazards);
        writer.WriteBool(PitSpikeHazards);
        writer.WriteU8(StockCount);
        writer.WriteU8(EpOption);
        writer.WriteBool(TeamAttack);
        writer.WriteU16(StageSelection);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static RulesUpdatePacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new RulesUpdatePacket(
            reader.ReadBool(),
            reader.ReadBool(),
            reader.ReadU8(),
            reader.ReadU8(),
            reader.ReadBool(),
            reader.ReadU16(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Submode / blind picks / handicap options packet (DataType = 8).
/// </summary>
public sealed record LobbyChangePacket(
    byte Submode,
    byte Subsubmode,
    bool BlindPicks,
    bool HandicapEnabled,
    string? PacketTag = "LOBBY RULES UPDATE DURING JOIN ACK")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(Submode);
        writer.WriteU8(Subsubmode);
        writer.WriteBool(BlindPicks);
        writer.WriteBool(HandicapEnabled);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static LobbyChangePacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new LobbyChangePacket(
            reader.ReadU8(),
            reader.ReadU8(),
            reader.ReadBool(),
            reader.ReadBool(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Broadcast when a new player completes join-ack (DataType = 5).
/// </summary>
public sealed record NewPlayerDataPacket(
    byte PlayerSlot,
    PlayerSummary Summary,
    string? PacketTag = "NEW PLAYER DATA")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        Summary.WriteTo(writer);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static NewPlayerDataPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new NewPlayerDataPacket(
            reader.ReadU8(),
            PlayerSummary.ReadFrom(reader),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Broadcast after a player leaves in lobby to update remaining slots and total count (DataType = 9).
/// </summary>
public sealed record ChangePlayerPortPacket(
    byte AssignedSlot,
    byte PlayerCount,
    string? PacketTag = "CHANGE_PLAYER_PORT")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(AssignedSlot);
        writer.WriteU8(PlayerCount);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static ChangePlayerPortPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ChangePlayerPortPacket(reader.ReadU8(), reader.ReadU8(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Broadcast per slot after lobby compaction (DataType = 10).
/// </summary>
public sealed record ChangePlayerDataPacket(
    byte PlayerSlot,
    string Name,
    short Character,
    byte Color,
    uint CustomPrimaryColor,
    uint CustomSecondaryColor,
    byte Skin,
    string Stage,
    bool Ready,
    byte SlideDownJump,
    byte HandicapStock,
    byte HandicapHealth,
    bool CheatEnabled,
    string? PacketTag = "CHANGE_PLAYER_DATA")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        writer.WriteGmlString(Name);
        writer.WriteS16(Character);
        writer.WriteU8(Color);
        writer.WriteU32(CustomPrimaryColor);
        writer.WriteU32(CustomSecondaryColor);
        writer.WriteU8(Skin);
        writer.WriteGmlString(Stage);
        writer.WriteBool(Ready);
        writer.WriteU8(SlideDownJump);
        writer.WriteU8(HandicapStock);
        writer.WriteU8(HandicapHealth);
        writer.WriteBool(CheatEnabled);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static ChangePlayerDataPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ChangePlayerDataPacket(
            reader.ReadU8(),
            reader.ReadGmlString(),
            reader.ReadS16(),
            reader.ReadU8(),
            reader.ReadU32(),
            reader.ReadU32(),
            reader.ReadU8(),
            reader.ReadGmlString(),
            reader.ReadBool(),
            reader.ReadU8(),
            reader.ReadU8(),
            reader.ReadU8(),
            reader.ReadBool(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Character/palette/skin/slide-jump selection packet (DataType = 6).
/// </summary>
public sealed record CharChangePacket(
    byte PlayerSlot,
    short Character,
    byte Color,
    uint CustomPrimaryColor,
    uint CustomSecondaryColor,
    byte Skin,
    byte SlideDownJump,
    string? PacketTag = null)
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        writer.WriteS16(Character);
        writer.WriteU8(Color);
        writer.WriteU32(CustomPrimaryColor);
        writer.WriteU32(CustomSecondaryColor);
        writer.WriteU8(Skin);
        writer.WriteU8(SlideDownJump);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static CharChangePacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new CharChangePacket(
            reader.ReadU8(),
            reader.ReadS16(),
            reader.ReadU8(),
            reader.ReadU32(),
            reader.ReadU32(),
            reader.ReadU8(),
            reader.ReadU8(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Stage preference update packet (DataType = 7).
/// </summary>
public sealed record StageChangePacket(byte PlayerSlot, string Stage, string? PacketTag = null)
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        writer.WriteGmlString(Stage);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static StageChangePacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new StageChangePacket(reader.ReadU8(), reader.ReadGmlString(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Squad roster packet (DataType = 11). Note GML omits any trailing diagnostic string.
/// </summary>
public sealed record SquadListPacket(
    byte PlayerSlot,
    byte Color,
    byte SlideDownJump,
    IReadOnlyList<ushort> Characters)
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        bool empty = Characters.Count == 0;
        writer.WriteU8((byte)(empty ? 1 : Characters.Count));
        writer.WriteU8(PlayerSlot);
        writer.WriteU8(Color);
        writer.WriteU8(SlideDownJump);
        if (empty)
        {
            writer.WriteU16(MmaConstants.EmptySquadSentinel);
        }
        else
        {
            foreach (ushort characterId in Characters)
            {
                writer.WriteU16(characterId);
            }
        }
    }

    public static SquadListPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        byte count = reader.ReadU8();
        byte slot = reader.ReadU8();
        byte color = reader.ReadU8();
        byte slideDownJump = reader.ReadU8();
        var raw = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            raw[i] = reader.ReadU16();
        }

        IReadOnlyList<ushort> characters =
            count == 0 || (count == 1 && raw[0] == MmaConstants.EmptySquadSentinel)
                ? []
                : raw;
        return new SquadListPacket(slot, color, slideDownJump, characters);
    }
}

/// <summary>
/// Request from a client to resend another player's squad list (DataType = 27).
/// </summary>
public sealed record SquadResendPacket(byte RequestingSlot, byte SquadSlot, string? PacketTag = "SQUAD_RESEND")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(RequestingSlot);
        writer.WriteU8(SquadSlot);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static SquadResendPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new SquadResendPacket(reader.ReadU8(), reader.ReadU8(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Per-player stock/health handicap packet (DataType = 31).
/// </summary>
public sealed record HandicapPacket(byte PlayerSlot, byte Stock, byte Health, string? PacketTag = "HANDICAP")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        writer.WriteU8(Stock);
        writer.WriteU8(Health);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static HandicapPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new HandicapPacket(reader.ReadU8(), reader.ReadU8(), reader.ReadU8(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Stage mode difficulty modifiers packet (DataType = 39).
/// </summary>
public sealed record StageDifficultiesPacket(
    byte EnemyDamage = 0,
    byte BossArmor = 0,
    byte StockReduction = 0,
    bool RemoveBlueFlags = false,
    bool RemoveDrops = false,
    bool SpikeInstakill = false,
    bool IncreasedEnemies = false,
    bool TeamAttack = false,
    string? PacketTag = "CHANGE_STAGE_DIFFICULTY")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(EnemyDamage);
        writer.WriteU8(BossArmor);
        writer.WriteU8(StockReduction);
        writer.WriteBool(RemoveBlueFlags);
        writer.WriteBool(RemoveDrops);
        writer.WriteBool(SpikeInstakill);
        writer.WriteBool(IncreasedEnemies);
        writer.WriteBool(TeamAttack);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static StageDifficultiesPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new StageDifficultiesPacket(
            reader.ReadU8(),
            reader.ReadU8(),
            reader.ReadU8(),
            reader.ReadBool(),
            reader.ReadBool(),
            reader.ReadBool(),
            reader.ReadBool(),
            reader.ReadBool(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Client-to-server ready signal (DataType = 14).
/// </summary>
public sealed record ClientReadyPacket(byte PlayerSlot, string? PacketTag = null)
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static ClientReadyPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ClientReadyPacket(reader.ReadU8(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Server-to-client ready broadcast (DataType = 14).
/// </summary>
public sealed record ServerPlayerReadyPacket(
    bool AllReady,
    byte PlayerSlot,
    short Character,
    byte Color,
    byte Skin,
    string Stage,
    string? PacketTag = "PLAYER READY")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBool(AllReady);
        writer.WriteU8(PlayerSlot);
        writer.WriteS16(Character);
        writer.WriteU8(Color);
        writer.WriteU8(Skin);
        writer.WriteGmlString(Stage);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static ServerPlayerReadyPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ServerPlayerReadyPacket(
            reader.ReadBool(),
            reader.ReadU8(),
            reader.ReadS16(),
            reader.ReadU8(),
            reader.ReadU8(),
            reader.ReadGmlString(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Client/Server unready notification (DataType = 21).
/// </summary>
public sealed record PlayerUnreadyPacket(byte PlayerSlot, string? PacketTag = null)
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static PlayerUnreadyPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new PlayerUnreadyPacket(reader.ReadU8(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Client-to-server stage confirmation after all players are ready (DataType = 15).
/// </summary>
public sealed record MatchReadyPacket(byte PlayerSlot, string StageChoice, string? PacketTag = "CLIENT READY")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        writer.WriteGmlString(StageChoice);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static MatchReadyPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new MatchReadyPacket(reader.ReadU8(), reader.ReadGmlString(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Server-to-client match start command (DataType = 15).
/// </summary>
public sealed record GotoLevelPacket(
    string Stage,
    ulong RandomSeed,
    byte Buffer,
    bool Hazards,
    bool PitSpikes,
    byte StockCount,
    bool TeamAttack,
    string? PacketTag = "GOTO LEVEL")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteGmlString(Stage);
        writer.WriteU64(RandomSeed);
        writer.WriteU8(Buffer);
        writer.WriteBool(Hazards);
        writer.WriteBool(PitSpikes);
        writer.WriteU8(StockCount);
        writer.WriteBool(TeamAttack);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static GotoLevelPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new GotoLevelPacket(
            reader.ReadGmlString(),
            reader.ReadU64(),
            reader.ReadU8(),
            reader.ReadBool(),
            reader.ReadBool(),
            reader.ReadU8(),
            reader.ReadBool(),
            reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Connection rejection packet (DataType = 19).
/// </summary>
public sealed record DisallowConnectPacket(DisallowConnectReason Reason, string? PacketTag = "DISALLOW_CONNECT")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8((byte)Reason);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static DisallowConnectPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new DisallowConnectPacket((DisallowConnectReason)reader.ReadU8(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Client-to-server forfeit trigger (DataType = 28).
/// </summary>
public sealed record ForfeitPacket(byte PlayerSlot, string? PacketTag = "MATCH_FORFEIT_CONTROL")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static ForfeitPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ForfeitPacket(reader.ReadU8(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Server-to-client synchronized forfeit frame broadcast (DataType = 29).
/// </summary>
public sealed record MatchForfeitPacket(byte PlayerSlot, uint FrameId, string? PacketTag = "MATCH_FORFEIT")
{
    public void WritePayload(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8(PlayerSlot);
        writer.WriteU32(FrameId);
        if (PacketTag is not null)
        {
            writer.WriteGmlString(PacketTag);
        }
    }

    public static MatchForfeitPacket ReadPayload(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new MatchForfeitPacket(reader.ReadU8(), reader.ReadU32(), reader.TryReadOptionalGmlString());
    }
}

/// <summary>
/// Single lockstep frame + controller bitmask entry.
/// </summary>
public readonly record struct InputFrameEntry(uint FrameId, uint Bitmask);

/// <summary>
/// Client-to-server lockstep input packet (DataType = 0), carrying sender's ClientMatchFrame.
/// May be sent with u16 SessionId on Control port (RUDP) or without SessionId on Input UDP port.
/// </summary>
public sealed record ClientInputPacket(
    byte Slot,
    uint ClientMatchFrame,
    IReadOnlyList<InputFrameEntry> Frames)
{
    public void WriteToControl(GmlBufferWriter writer, ushort sessionId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        new MmaPacketHeader(sessionId, (byte)MmaPacketType.Input).WriteTo(writer);
        WriteBody(writer);
    }

    public void WriteToFastInput(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8((byte)MmaPacketType.Input);
        WriteBody(writer);
    }

    public void WriteBody(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Frames.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Frames), "Input frame count cannot exceed 255.");
        }

        writer.WriteU8((byte)Frames.Count);
        writer.WriteU8(Slot);
        writer.WriteU32(ClientMatchFrame);
        foreach (InputFrameEntry entry in Frames)
        {
            writer.WriteU32(entry.FrameId);
            writer.WriteU32(entry.Bitmask);
        }
    }

    public static ClientInputPacket ReadBody(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        byte count = reader.ReadU8();
        byte slot = reader.ReadU8();
        uint clientMatchFrame = reader.ReadU32();
        var entries = new InputFrameEntry[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = new InputFrameEntry(reader.ReadU32(), reader.ReadU32());
        }

        return new ClientInputPacket(slot, clientMatchFrame, entries);
    }
}

/// <summary>
/// Server-to-client lockstep input relay packet (DataType = 0), WITHOUT ClientMatchFrame.
/// </summary>
public sealed record RelayInputPacket(
    byte Slot,
    IReadOnlyList<InputFrameEntry> Frames)
{
    public void WriteToControl(GmlBufferWriter writer, ushort sessionId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        new MmaPacketHeader(sessionId, (byte)MmaPacketType.Input).WriteTo(writer);
        WriteBody(writer);
    }

    public void WriteToFastInput(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU8((byte)MmaPacketType.Input);
        WriteBody(writer);
    }

    public void WriteBody(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Frames.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Frames), "Input frame count cannot exceed 255.");
        }

        writer.WriteU8((byte)Frames.Count);
        writer.WriteU8(Slot);
        foreach (InputFrameEntry entry in Frames)
        {
            writer.WriteU32(entry.FrameId);
            writer.WriteU32(entry.Bitmask);
        }
    }

    public static RelayInputPacket ReadBody(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        byte count = reader.ReadU8();
        byte slot = reader.ReadU8();
        var entries = new InputFrameEntry[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = new InputFrameEntry(reader.ReadU32(), reader.ReadU32());
        }

        return new RelayInputPacket(slot, entries);
    }
}
