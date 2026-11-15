using System.IO;
using Mma.Protocol;

namespace Mma.Protocol.Tests;

public sealed class GmlBufferTests
{
    [Fact]
    public void RoundTrips_AllPrimitives_AndPacketHeader()
    {
        var writer = new GmlBufferWriter();
        var header = new MmaPacketHeader(SessionId: 7087, DataType: (byte)MmaPacketType.NewPlayerWelcome);

        header.WriteTo(writer);
        writer.WriteU8(250);
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.WriteU16(6528);
        writer.WriteS16(-1234);
        writer.WriteU32(0x12345678);
        writer.WriteGmlString("MegaMan_洛克人");
        writer.WriteGmlString(string.Empty);

        var reader = new GmlBufferReader(writer.WrittenMemory);
        Assert.Equal(header, MmaPacketHeader.ReadFrom(reader));
        Assert.Equal((byte)250, reader.ReadU8());
        Assert.True(reader.ReadBool());
        Assert.False(reader.ReadBool());
        Assert.Equal((ushort)6528, reader.ReadU16());
        Assert.Equal((short)-1234, reader.ReadS16());
        Assert.Equal(0x12345678u, reader.ReadU32());
        Assert.Equal("MegaMan_洛克人", reader.ReadGmlString());
        Assert.Equal(string.Empty, reader.ReadGmlString());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Writes_LittleEndian_AndNullTerminatedString()
    {
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(0x1234, (byte)MmaPacketType.PlayerReady).WriteTo(writer);
        writer.WriteGmlString("OK");

        byte[] expected = [0x34, 0x12, 14, (byte)'O', (byte)'K', 0x00];
        Assert.Equal(expected, writer.ToArray());
    }

    [Fact]
    public void Throws_OnTruncatedOrUnterminatedInput()
    {
        var shortReader = new GmlBufferReader(new byte[] { 0x01 });
        Assert.Throws<EndOfStreamException>(() => shortReader.ReadU16());

        var unterminatedStringReader = new GmlBufferReader("ABC"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => unterminatedStringReader.ReadGmlString());

        var writer = new GmlBufferWriter();
        Assert.Throws<ArgumentException>(() => writer.WriteGmlString("A\0B"));
    }
}
