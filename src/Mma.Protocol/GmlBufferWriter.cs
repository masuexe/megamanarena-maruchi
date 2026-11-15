using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Mma.Protocol;

/// <summary>
/// Writes GameMaker Studio little-endian binary buffer primitives.
/// </summary>
public sealed class GmlBufferWriter
{
    private readonly ArrayBufferWriter<byte> _buffer;

    public GmlBufferWriter(int initialCapacity = 256)
    {
        _buffer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int WrittenCount => _buffer.WrittenCount;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public void Reset() => _buffer.ResetWrittenCount();

    public void WriteU8(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    public void WriteBool(bool value) => WriteU8(value ? (byte)1 : (byte)0);

    public void WriteU16(ushort value)
    {
        var span = _buffer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        _buffer.Advance(sizeof(ushort));
    }

    public void WriteS16(short value)
    {
        var span = _buffer.GetSpan(sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span, value);
        _buffer.Advance(sizeof(short));
    }

    public void WriteU32(uint value)
    {
        var span = _buffer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        _buffer.Advance(sizeof(uint));
    }

    public void WriteU64(ulong value)
    {
        var span = _buffer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        _buffer.Advance(sizeof(ulong));
    }

    /// <summary>
    /// Writes a GameMaker buffer_string (UTF-8 bytes terminated by a single 0x00 byte).
    /// </summary>
    public void WriteGmlString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("GML buffer_string cannot contain embedded NUL characters.", nameof(value));
        }

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length) + 1;
        var span = _buffer.GetSpan(maxByteCount);
        int bytesWritten = Encoding.UTF8.GetBytes(value, span);
        span[bytesWritten] = 0;
        _buffer.Advance(bytesWritten + 1);
    }
}
