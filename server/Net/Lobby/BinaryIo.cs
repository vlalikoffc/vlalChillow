using System.Buffers.Binary;
using System.Text;

namespace StandChillow.LanServer.Net.Lobby;

/// <summary>Chillow fzr/fzo-compatible writer (UTF-8 + 7-bit varint lengths, LE ints).</summary>
public sealed class LobbyWriter
{
    private readonly MemoryStream _ms = new(256);

    public byte[] ToArray() => _ms.ToArray();
    public int Length => (int)_ms.Length;

    public void WriteByte(byte v) => _ms.WriteByte(v);

    public void WriteBool(bool v) => _ms.WriteByte(v ? (byte)1 : (byte)0);

    public void WriteInt16(short v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, v);
        _ms.Write(b);
    }

    public void WriteInt32(int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        _ms.Write(b);
    }

    public void WriteString(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        WriteVarInt(bytes.Length);
        _ms.Write(bytes);
    }

    public void WriteBytes(ReadOnlySpan<byte> data) => _ms.Write(data);

    /// <summary>fzs.bokf — bare property bag (profile props).</summary>
    public void WriteBareEmptyProps() => WriteInt16(0);

    /// <summary>fzs.bokf — bare bag (int16 count + key/fzp entries). Used by match <c>gap</c> room props + SetProperties.</summary>
    public void WriteBareProps(IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        WriteInt16(checked((short)props.Count));
        foreach (var (key, value) in props)
        {
            WriteString(key);
            WriteFzp(value);
        }
    }

    /// <summary>fzr.boju → fzu.bolm(writeTypeCode:true) of empty fzs — lobby/member props.</summary>
    public void WriteTypedEmptyProps()
    {
        WriteByte(10); // fzu.Code.PropertiesRecord
        WriteInt16(0);
    }

    /// <summary>
    /// Typed lobby/member props (fzr.boju): PropertiesRecord + bare bag.
    /// Each value is an fzp/fzq variant (live JoinResponse: Reference→String for LobbyId/GameModeId).
    /// </summary>
    public void WriteTypedProps(IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        WriteByte(10); // fzu.Code.PropertiesRecord
        WriteInt16(checked((short)props.Count));
        foreach (var (key, value) in props)
        {
            WriteString(key);
            WriteFzp(value);
        }
    }

    /// <summary>fzp.boio — fzq type tag + payload (Bool=6, Int=1, Byte=255, Reference=10 → nested fzu, …).</summary>
    public void WriteFzp(LobbyVariant v)
    {
        switch (v.Kind)
        {
            case LobbyVariantKind.Bool:
                WriteByte(6); // fzq.Bool
                WriteByte(v.Bool ? (byte)1 : (byte)0);
                break;
            case LobbyVariantKind.Int:
                WriteByte(1); // fzq.Int
                WriteInt32(v.Int);
                break;
            case LobbyVariantKind.Double:
                WriteByte(5); // fzq.Double
                WriteDouble(v.Double);
                break;
            case LobbyVariantKind.Byte:
                WriteByte(255); // fzq.Byte
                WriteByte(v.Byte);
                break;
            case LobbyVariantKind.String:
                WriteByte(10); // fzq.Reference
                WriteByte(13); // fzu.String
                WriteString(v.String);
                break;
            case LobbyVariantKind.StringArray:
                WriteByte(10); // fzq.Reference
                WriteByte(14); // fzu.StringArray
                WriteInt16(checked((short)(v.Strings?.Count ?? 0)));
                if (v.Strings is not null)
                {
                    foreach (var s in v.Strings)
                        WriteString(s);
                }
                break;
            case LobbyVariantKind.Properties:
                WriteByte(10); // fzq.Reference
                WriteByte(10); // fzu.PropertiesRecord
                WriteInt16(checked((short)(v.Props?.Count ?? 0)));
                if (v.Props is not null)
                {
                    foreach (var (key, nested) in v.Props)
                    {
                        WriteString(key);
                        WriteFzp(nested);
                    }
                }
                break;
            case LobbyVariantKind.ByteArray:
                WriteByte(10); // fzq.Reference
                WriteByte(4); // fzu.ByteArray
                WriteInt32(v.Bytes?.Length ?? 0);
                if (v.Bytes is { Length: > 0 } bytes)
                    WriteBytes(bytes);
                break;
            case LobbyVariantKind.Null:
                WriteByte(10); // fzq.Reference
                WriteByte(255); // fzu.Null
                break;
            default:
                throw new InvalidOperationException($"Cannot write LobbyVariant kind {v.Kind}");
        }
    }

    public void WriteFloat(float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        _ms.Write(b);
    }

    public void WriteDouble(double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        _ms.Write(b);
    }

    private void WriteVarInt(int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            _ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        _ms.WriteByte((byte)v);
    }
}

/// <summary>Chillow fzo-compatible reader.</summary>
public sealed class LobbyReader
{
    private readonly byte[] _data;
    private int _pos;

    public LobbyReader(byte[] data) => _data = data;
    public LobbyReader(ReadOnlySpan<byte> data) => _data = data.ToArray();

    public int Remaining => _data.Length - _pos;
    public int Position => _pos;

    public byte ReadByte()
    {
        Ensure(1);
        return _data[_pos++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public short ReadInt16()
    {
        Ensure(2);
        var v = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(_pos));
        _pos += 2;
        return v;
    }

    public int ReadInt32()
    {
        Ensure(4);
        var v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public float ReadFloat()
    {
        Ensure(4);
        var v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public double ReadDouble()
    {
        Ensure(8);
        var v = BinaryPrimitives.ReadDoubleLittleEndian(_data.AsSpan(_pos));
        _pos += 8;
        return v;
    }

    public string ReadString()
    {
        var len = ReadVarInt();
        if (len < 0 || len > Remaining)
            throw new InvalidDataException($"Bad string length {len} remaining={Remaining}");
        var s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    public byte[] ReadBytes(int length)
    {
        if (length < 0 || length > Remaining)
            throw new InvalidDataException($"Bad byte length {length}");
        var buf = new byte[length];
        Buffer.BlockCopy(_data, _pos, buf, 0, length);
        _pos += length;
        return buf;
    }

    /// <summary>Skip fzs.bokg / bokf payload (int16 count + entries).</summary>
    public void SkipBareProps()
    {
        var count = ReadInt16();
        if (count < 0)
            throw new InvalidDataException($"Bad prop count {count}");
        for (var i = 0; i < count; i++)
        {
            _ = ReadString();
            SkipVariant();
        }
    }

    /// <summary>Read fzs.bokf bare bag (int16 count + key/fzp entries).</summary>
    public List<(string Key, LobbyVariant Value)> ReadBareProps()
    {
        var count = ReadInt16();
        if (count < 0 || count > 512)
            throw new InvalidDataException($"Bad bare prop count {count}");
        var list = new List<(string, LobbyVariant)>(count);
        for (var i = 0; i < count; i++)
            list.Add((ReadString(), ReadFzp()));
        return list;
    }

    /// <summary>Skip typed props: fzu Code.PropertiesRecord (10) + bare bag.</summary>
    public void SkipTypedProps()
    {
        _ = ReadTypedProps();
    }

    /// <summary>Read typed props bag (JoinResponse lobby/member). Values are fzp/fzq-coded.</summary>
    public List<(string Key, LobbyVariant Value)> ReadTypedProps()
    {
        var code = ReadByte();
        if (code != 10)
            throw new InvalidDataException($"Expected typed props code 10, got {code}");
        var count = ReadInt16();
        if (count < 0 || count > 256)
            throw new InvalidDataException($"Bad typed prop count {count}");
        var list = new List<(string, LobbyVariant)>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadString();
            list.Add((key, ReadFzp()));
        }
        return list;
    }

    /// <summary>fzp.boio reader — fzq tag then payload.</summary>
    public LobbyVariant ReadFzp()
    {
        var code = ReadByte();
        switch (code)
        {
            case 6: // fzq.Bool
                return LobbyVariant.FromBool(ReadByte() != 0);
            case 1: // fzq.Int
                return LobbyVariant.FromInt(ReadInt32());
            case 2: // fzq.Short
                return LobbyVariant.FromInt(ReadInt16());
            case 5: // fzq.Double
                return LobbyVariant.FromDouble(ReadDouble());
            case 255: // fzq.Byte
                return LobbyVariant.FromByte(ReadByte());
            case 10: // fzq.Reference → nested fzu
                return ReadFzuValue();
            case 0: // None
                return LobbyVariant.Null();
            default:
                // Some writers may emit fzu codes directly; accept common ones.
                _pos--;
                return ReadFzuValue();
        }
    }

    private LobbyVariant ReadFzuValue()
    {
        var code = ReadByte();
        switch (code)
        {
            case 2: // Boolean
                return LobbyVariant.FromBool(ReadByte() != 0);
            case 3: // Byte
                return LobbyVariant.FromInt(ReadByte());
            case 6: // Short
                return LobbyVariant.FromInt(ReadInt16());
            case 9: // Integer
                return LobbyVariant.FromInt(ReadInt32());
            case 13: // String
                return LobbyVariant.FromString(ReadString());
            case 14: // StringArray
            {
                var n = ReadInt16();
                if (n < 0 || n > 256)
                    throw new InvalidDataException($"Bad StringArray count {n}");
                var arr = new List<string>(n);
                for (var i = 0; i < n; i++)
                    arr.Add(ReadString());
                return LobbyVariant.FromStrings(arr);
            }
            case 10: // PropertiesRecord
            {
                var n = ReadInt16();
                if (n < 0 || n > 256)
                    throw new InvalidDataException($"Bad nested prop count {n}");
                var props = new List<(string, LobbyVariant)>(n);
                for (var i = 0; i < n; i++)
                    props.Add((ReadString(), ReadFzp()));
                return LobbyVariant.FromProps(props);
            }
            case 4: // ByteArray
            {
                var len = ReadInt32();
                if (len < 0 || len > 16 * 1024 * 1024)
                    throw new InvalidDataException($"Bad ByteArray len {len}");
                return LobbyVariant.FromBytes(ReadBytes(len));
            }
            case 255: // Null
                return LobbyVariant.Null();
            default:
                throw new InvalidDataException($"Unsupported fzu code {code} at {_pos}");
        }
    }

    /// <summary>fzp.boio / fzu object — best-effort skip for join-request props.</summary>
    public void SkipVariant()
    {
        var code = ReadByte();
        switch (code)
        {
            case 1: // Int (fzq.Int) or Array depending on context — fzp uses fzq
                _ = ReadInt32();
                break;
            case 2: // Short
                _ = ReadInt16();
                break;
            case 3: // Long
                Ensure(8);
                _pos += 8;
                break;
            case 4: // Float
                Ensure(4);
                _pos += 4;
                break;
            case 5: // Double
                Ensure(8);
                _pos += 8;
                break;
            case 6: // Bool
                _ = ReadByte();
                break;
            case 7: // Vector2
                Ensure(8);
                _pos += 8;
                break;
            case 8: // Vector3
                Ensure(12);
                _pos += 12;
                break;
            case 9: // Quaternion
                Ensure(16);
                _pos += 16;
                break;
            case 10: // Reference → nested fzu object
                SkipFzuObject();
                break;
            case 255: // Byte
                _ = ReadByte();
                break;
            case 0: // None
                break;
            default:
                // fzu.Code path when type-tagged differently — try fzu codes
                SkipFzuByCode(code);
                break;
        }
    }

    private void SkipFzuObject()
    {
        var code = ReadByte();
        SkipFzuByCode(code);
    }

    private void SkipFzuByCode(byte code)
    {
        switch (code)
        {
            case 2: // Boolean
            case 3: // Byte
                _ = ReadByte();
                break;
            case 6: // Short
                _ = ReadInt16();
                break;
            case 7: // Float
            case 9: // Integer
                Ensure(4);
                _pos += 4;
                break;
            case 8: // Double
            case 12: // Long
                Ensure(8);
                _pos += 8;
                break;
            case 10: // PropertiesRecord
                SkipBareProps();
                break;
            case 13: // String
                _ = ReadString();
                break;
            case 4: // ByteArray
            {
                var len = ReadInt32();
                _ = ReadBytes(len);
                break;
            }
            case 255: // Null
                break;
            default:
                throw new InvalidDataException($"Unsupported variant/fzu code {code} at {_pos}");
        }
    }

    private int ReadVarInt()
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            Ensure(1);
            var b = _data[_pos++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 35)
                throw new InvalidDataException("VarInt too long");
        }
    }

    private void Ensure(int n)
    {
        if (Remaining < n)
            throw new InvalidDataException($"Need {n} bytes, have {Remaining}");
    }
}
