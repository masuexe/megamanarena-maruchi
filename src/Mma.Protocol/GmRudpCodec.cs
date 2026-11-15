using System.Buffers.Binary;

namespace Mma.Protocol;

/// <summary>
/// GameMaker reliable UDP packet kind identifier (bytes 4..7 LE).
/// </summary>
public enum GmRudpKind : uint
{
    Data = 1,
    Ack = 2
}

/// <summary>
/// Decoded 12-byte GameMaker reliable UDP envelope.
/// </summary>
public readonly record struct GmRudpFrame(
    GmRudpKind Kind,
    uint SequenceNumber,
    ReadOnlyMemory<byte> Payload,
    IReadOnlyList<uint> AckedSequences);

/// <summary>
/// Codec for GameMaker's network_config_enable_reliable_udp 12-byte UDP framing:
/// [u32 LE ~Crc32(bytes[4..])][u32 LE kind (1=DATA, 2=ACK)][u32 LE seqNum][body].
/// </summary>
public static class GmRudpCodec
{
    public const int HeaderSize = 12;
    public const int MaxAckEntriesPerPacket = 62;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>
    /// Computes GameMaker's bitwise-inverted IEEE 802.3 CRC-32 (~CRC32) over <paramref name="bodyAfterChecksum"/>.
    /// </summary>
    public static uint ComputeChecksum(ReadOnlySpan<byte> bodyAfterChecksum)
    {
        uint crc = 0xFFFF_FFFFu;
        for (int i = 0; i < bodyAfterChecksum.Length; i++)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ bodyAfterChecksum[i]) & 0xFF];
        }

        // Standard IEEE CRC-32 XORs with 0xFFFFFFFF at the end; ~CRC32 omits that final inversion.
        return crc;
    }

    public static byte[] EncodeData(uint sequenceNumber, ReadOnlySpan<byte> payload)
    {
        if (sequenceNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "GM RUDP sequence numbers are 1-based.");
        }

        byte[] packet = new byte[HeaderSize + payload.Length];
        Span<byte> span = packet;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)GmRudpKind.Data);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), sequenceNumber);
        payload.CopyTo(span.Slice(HeaderSize));

        uint checksum = ComputeChecksum(span.Slice(4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), checksum);
        return packet;
    }

    public static byte[] EncodeAck(uint sequenceNumber, ReadOnlySpan<uint> ackedSequences)
    {
        if (sequenceNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "GM RUDP sequence numbers are 1-based.");
        }

        if (ackedSequences.Length == 0 || ackedSequences.Length > MaxAckEntriesPerPacket)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ackedSequences),
                $"ACK packet must contain between 1 and {MaxAckEntriesPerPacket} sequence numbers.");
        }

        byte[] packet = new byte[HeaderSize + (ackedSequences.Length * sizeof(uint))];
        Span<byte> span = packet;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)GmRudpKind.Ack);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), sequenceNumber);

        for (int i = 0; i < ackedSequences.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(HeaderSize + (i * sizeof(uint)), 4), ackedSequences[i]);
        }

        uint checksum = ComputeChecksum(span.Slice(4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), checksum);
        return packet;
    }

    public static bool TryDecode(ReadOnlyMemory<byte> datagram, out GmRudpFrame frame)
    {
        ReadOnlySpan<byte> span = datagram.Span;
        if (span.Length < HeaderSize)
        {
            frame = default;
            return false;
        }

        uint rawKind = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        if (rawKind is not ((uint)GmRudpKind.Data or (uint)GmRudpKind.Ack))
        {
            frame = default;
            return false;
        }

        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
        if (seq == 0)
        {
            frame = default;
            return false;
        }

        var kind = (GmRudpKind)rawKind;
        int bodyLength = span.Length - HeaderSize;
        if (kind == GmRudpKind.Data && bodyLength == 0)
        {
            frame = default;
            return false;
        }

        if (kind == GmRudpKind.Ack && (bodyLength < sizeof(uint) || (bodyLength % sizeof(uint)) != 0))
        {
            frame = default;
            return false;
        }

        uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
        uint actualChecksum = ComputeChecksum(span.Slice(4));
        if (expectedChecksum != actualChecksum)
        {
            frame = default;
            return false;
        }

        if (kind == GmRudpKind.Data)
        {
            frame = new GmRudpFrame(kind, seq, datagram.Slice(HeaderSize), []);
            return true;
        }

        int ackCount = bodyLength / sizeof(uint);
        var acks = new uint[ackCount];
        for (int i = 0; i < ackCount; i++)
        {
            acks[i] = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(HeaderSize + (i * sizeof(uint)), 4));
        }

        frame = new GmRudpFrame(kind, seq, ReadOnlyMemory<byte>.Empty, acks);
        return true;
    }

    private static uint[] BuildCrc32Table()
    {
        const uint polynomial = 0xEDB8_8320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1u) != 0 ? (entry >> 1) ^ polynomial : (entry >> 1);
            }

            table[i] = entry;
        }

        return table;
    }
}
