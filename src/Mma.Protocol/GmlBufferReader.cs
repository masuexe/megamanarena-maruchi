using System.Buffers.Binary;
using System.Text;

namespace Mma.Protocol;

/// <summary>
/// Reads GameMaker Studio little-endian binary buffer primitives.
/// </summary>
public sealed class GmlBufferReader
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _offset;

    public GmlBufferReader(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
    }

    public int Offset => _offset;

    public int Remaining => _buffer.Length - _offset;

    public byte ReadU8()
    {
        EnsureRemaining(1);
        return _buffer.Span[_offset++];
    }

    public bool ReadBool() => ReadU8() != 0;

    public ushort ReadU16()
    {
        EnsureRemaining(sizeof(ushort));
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Span[_offset..]);
        _offset += sizeof(ushort);
        return value;
    }

    public short ReadS16()
    {
        EnsureRemaining(sizeof(short));
        short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Span[_offset..]);
        _offset += sizeof(short);
        return value;
    }

    public uint ReadU32()
    {
        EnsureRemaining(sizeof(uint));
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Span[_offset..]);
        _offset += sizeof(uint);
        return value;
    }

    public ulong ReadU64()
    {
        EnsureRemaining(sizeof(ulong));
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Span[_offset..]);
        _offset += sizeof(ulong);
        return value;
    }

    /// <summary>
    /// Reads a GameMaker buffer_string (NUL-terminated UTF-8 bytes).
    /// </summary>
    public string ReadGmlString()
    {
        var remaining = _buffer.Span[_offset..];
        int terminatorIndex = remaining.IndexOf((byte)0);
        if (terminatorIndex < 0)
        {
            throw new InvalidDataException("Unterminated GML buffer_string: missing 0x00 terminator.");
        }

        string text = Encoding.UTF8.GetString(remaining[..terminatorIndex]);
        _offset += terminatorIndex + 1;
        return text;
    }

    /// <summary>
    /// Reads an optional trailing GML string tag if unread bytes remain.
    /// </summary>
    public string? TryReadOptionalGmlString()
    {
        return Remaining > 0 ? ReadGmlString() : null;
    }

    private void EnsureRemaining(int requiredBytes)
    {
        if (Remaining < requiredBytes)
        {
            throw new EndOfStreamException(
                $"Unexpected end of GML buffer: needed {requiredBytes} bytes, but only {Remaining} remain at offset {_offset}.");
        }
    }
}
