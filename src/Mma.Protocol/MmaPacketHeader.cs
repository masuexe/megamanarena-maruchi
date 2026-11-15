namespace Mma.Protocol;

/// <summary>
/// Standard 3-byte GameMaker UDP packet header: u16 session ID followed by u8 packet data type.
/// </summary>
public readonly record struct MmaPacketHeader(ushort SessionId, byte DataType)
{
    public const int ByteSize = sizeof(ushort) + sizeof(byte);

    public MmaPacketType KnownType => (MmaPacketType)DataType;

    public void WriteTo(GmlBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteU16(SessionId);
        writer.WriteU8(DataType);
    }

    public static MmaPacketHeader ReadFrom(GmlBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ushort sessionId = reader.ReadU16();
        byte dataType = reader.ReadU8();
        return new MmaPacketHeader(sessionId, dataType);
    }
}
