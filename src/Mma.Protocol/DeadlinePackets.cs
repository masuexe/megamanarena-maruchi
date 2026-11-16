using System.Buffers.Binary;

namespace Mma.Protocol;

/// <summary>
/// Single authoritative frame snapshot containing controller bitmasks for all active players in a deadline batch.
/// </summary>
public readonly record struct DeadlineFrameSnapshot(uint FrameId, uint[] PlayerMasks);

/// <summary>
/// Client-to-server deadline proposal packet (DataType = 240) sent on Control RUDP socket.
/// Wire format: [u16 sessionId][u8 240][u32 token][u16 phase][u32 ackFrame][u8 count][count * (u32 frame, u32 bitmask)].
/// </summary>
public sealed record DeadlineProposalPacket(
    uint Token,
    ushort Phase,
    uint AckFrame,
    IReadOnlyList<InputFrameEntry> Proposals)
{
    public void WriteTo(GmlBufferWriter writer, ushort sessionId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Proposals.Count > MmaConstants.DeadlineMaxProposalCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Proposals),
                $"Deadline proposal count cannot exceed {MmaConstants.DeadlineMaxProposalCount}.");
        }

        new MmaPacketHeader(sessionId, (byte)MmaPacketType.DeadlineTimeline).WriteTo(writer);
        writer.WriteU32(Token);
        writer.WriteU16(Phase);
        writer.WriteU32(AckFrame);
        writer.WriteU8((byte)Proposals.Count);
        for (int i = 0; i < Proposals.Count; i++)
        {
            writer.WriteU32(Proposals[i].FrameId);
            writer.WriteU32(Proposals[i].Bitmask);
        }
    }

    public static bool TryReadPayload(ReadOnlySpan<byte> fullDatagram, out DeadlineProposalPacket? packet)
    {
        packet = null;
        if (fullDatagram.Length < MmaConstants.DeadlineHeaderSize ||
            fullDatagram[2] != (byte)MmaPacketType.DeadlineTimeline)
        {
            return false;
        }

        byte count = fullDatagram[13];
        if (count > MmaConstants.DeadlineMaxProposalCount ||
            fullDatagram.Length != MmaConstants.DeadlineHeaderSize + (count * 8))
        {
            return false;
        }

        uint token = BinaryPrimitives.ReadUInt32LittleEndian(fullDatagram.Slice(3, 4));
        ushort phase = BinaryPrimitives.ReadUInt16LittleEndian(fullDatagram.Slice(7, 2));
        uint ackFrame = BinaryPrimitives.ReadUInt32LittleEndian(fullDatagram.Slice(9, 4));
        var proposals = new InputFrameEntry[count];
        int offset = MmaConstants.DeadlineHeaderSize;
        for (int i = 0; i < count; i++)
        {
            uint frame = BinaryPrimitives.ReadUInt32LittleEndian(fullDatagram.Slice(offset, 4));
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(fullDatagram.Slice(offset + 4, 4));
            proposals[i] = new InputFrameEntry(frame, mask);
            offset += 8;
        }

        packet = new DeadlineProposalPacket(token, phase, ackFrame, proposals);
        return true;
    }
}

/// <summary>
/// Server-to-client authoritative deadline batch packet (DataType = 240) sent on Control RUDP socket.
/// Wire format: [u16 sessionId][u8 240][u32 token][u16 phase][u32 highestCommitted][u8 count][count * (u32 frame, playerCount * u32 bitmask)].
/// </summary>
public sealed record DeadlineBatchPacket(
    uint Token,
    ushort Phase,
    uint HighestCommitted,
    int PlayerCount,
    IReadOnlyList<DeadlineFrameSnapshot> Frames)
{
    public void WriteTo(GmlBufferWriter writer, ushort sessionId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (PlayerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PlayerCount));
        }
        if (Frames.Count > MmaConstants.DeadlineMaxBatchCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Frames),
                $"Deadline batch frame count cannot exceed {MmaConstants.DeadlineMaxBatchCount}.");
        }

        new MmaPacketHeader(sessionId, (byte)MmaPacketType.DeadlineTimeline).WriteTo(writer);
        writer.WriteU32(Token);
        writer.WriteU16(Phase);
        writer.WriteU32(HighestCommitted);
        writer.WriteU8((byte)Frames.Count);
        for (int i = 0; i < Frames.Count; i++)
        {
            DeadlineFrameSnapshot snapshot = Frames[i];
            if (snapshot.PlayerMasks.Length != PlayerCount)
            {
                throw new ArgumentException(
                    $"Expected {PlayerCount} player masks for frame {snapshot.FrameId}, got {snapshot.PlayerMasks.Length}.",
                    nameof(Frames));
            }

            writer.WriteU32(snapshot.FrameId);
            for (int slot = 0; slot < PlayerCount; slot++)
            {
                writer.WriteU32(snapshot.PlayerMasks[slot]);
            }
        }
    }

    public static bool TryReadPayload(ReadOnlySpan<byte> fullDatagram, int playerCount, out DeadlineBatchPacket? packet)
    {
        packet = null;
        if (playerCount < 1 ||
            fullDatagram.Length < MmaConstants.DeadlineHeaderSize ||
            fullDatagram[2] != (byte)MmaPacketType.DeadlineTimeline)
        {
            return false;
        }

        byte count = fullDatagram[13];
        int frameStride = 4 + (playerCount * 4);
        if (count > MmaConstants.DeadlineMaxBatchCount ||
            fullDatagram.Length != MmaConstants.DeadlineHeaderSize + (count * frameStride))
        {
            return false;
        }

        return TryReadBody(fullDatagram[MmaPacketHeader.ByteSize..], playerCount, out packet);
    }

    public static bool TryReadBody(ReadOnlySpan<byte> bodyAfterHeader, int playerCount, out DeadlineBatchPacket? packet)
    {
        packet = null;
        const int bodyHeaderSize = MmaConstants.DeadlineHeaderSize - MmaPacketHeader.ByteSize; // 11 bytes
        if (playerCount < 1 || bodyAfterHeader.Length < bodyHeaderSize)
        {
            return false;
        }

        byte count = bodyAfterHeader[10];
        int frameStride = 4 + (playerCount * 4);
        if (count > MmaConstants.DeadlineMaxBatchCount ||
            bodyAfterHeader.Length != bodyHeaderSize + (count * frameStride))
        {
            return false;
        }

        uint token = BinaryPrimitives.ReadUInt32LittleEndian(bodyAfterHeader.Slice(0, 4));
        ushort phase = BinaryPrimitives.ReadUInt16LittleEndian(bodyAfterHeader.Slice(4, 2));
        uint highestCommitted = BinaryPrimitives.ReadUInt32LittleEndian(bodyAfterHeader.Slice(6, 4));
        var frames = new DeadlineFrameSnapshot[count];
        int offset = bodyHeaderSize;
        for (int i = 0; i < count; i++)
        {
            uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(bodyAfterHeader.Slice(offset, 4));
            offset += 4;
            uint[] masks = new uint[playerCount];
            for (int slot = 0; slot < playerCount; slot++)
            {
                masks[slot] = BinaryPrimitives.ReadUInt32LittleEndian(bodyAfterHeader.Slice(offset, 4));
                offset += 4;
            }
            frames[i] = new DeadlineFrameSnapshot(frameId, masks);
        }

        packet = new DeadlineBatchPacket(token, phase, highestCommitted, playerCount, frames);
        return true;
    }
}
