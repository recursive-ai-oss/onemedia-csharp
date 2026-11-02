namespace RecursiveAI.OneMedia.Metadata.Telemetry.Dji;

/// <summary>The wire type of a protocol buffer field (the low three bits of its key).</summary>
public enum ProtobufWireType
{
    /// <summary>A base-128 varint (int32, int64, uint32, uint64, sint32, sint64, bool, enum).</summary>
    Varint = 0,

    /// <summary>Eight bytes, little-endian (fixed64, sfixed64, double).</summary>
    Fixed64 = 1,

    /// <summary>A varint length and that many bytes (string, bytes, embedded message, packed repeated).</summary>
    LengthDelimited = 2,

    /// <summary>Four bytes, little-endian (fixed32, sfixed32, float).</summary>
    Fixed32 = 5,
}

/// <summary>
/// Walks the fields of one protocol buffer message without a schema: field number, wire type and the value bytes. It is the generic layer
/// under the DJI <c>djmd</c>/<c>dbgi</c> decoder, so fields this library does not map stay reachable. Groups (wire types 3 and 4) and
/// reserved wire types end the walk, as does a length that runs past the data. Nothing is allocated.
/// </summary>
public ref struct ProtobufReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    /// <summary>Creates a reader over one message.</summary>
    /// <param name="message">The message bytes.</param>
    public ProtobufReader(ReadOnlySpan<byte> message)
    {
        _data = message;
        _pos = 0;
    }

    /// <summary>Offset of the next field in the message.</summary>
    public readonly int Position => _pos;

    /// <summary>Reads the next field.</summary>
    /// <param name="number">The field number (1 to 536,870,911).</param>
    /// <param name="wireType">The wire type.</param>
    /// <param name="varint">For a varint, its value; for a fixed-width field, its bits (zero-extended).</param>
    /// <param name="payload">For a length-delimited field, its bytes; for a fixed-width field, its raw bytes; else empty.</param>
    /// <param name="start">Offset of the field's key in the message.</param>
    /// <returns>False at the end of the message or at bytes that are not a valid field (the rest is then ignored).</returns>
    public bool TryRead(out int number, out ProtobufWireType wireType, out ulong varint, out ReadOnlySpan<byte> payload, out int start)
    {
        number = 0;
        wireType = 0;
        varint = 0;
        payload = default;
        start = _pos;
        if (_pos >= _data.Length || !TryVarint(out ulong key))
        {
            return false;
        }

        ulong num = key >> 3;
        int wt = (int)(key & 7);
        if (num == 0 || num > 0x1FFFFFFF)
        {
            return Fail();
        }

        number = (int)num;
        wireType = (ProtobufWireType)wt;
        switch (wt)
        {
            case 0:
                return TryVarint(out varint) || Fail();
            case 1:
                if (_data.Length - _pos < 8)
                {
                    return Fail();
                }

                payload = _data.Slice(_pos, 8);
                varint = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload);
                _pos += 8;
                return true;
            case 5:
                if (_data.Length - _pos < 4)
                {
                    return Fail();
                }

                payload = _data.Slice(_pos, 4);
                varint = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload);
                _pos += 4;
                return true;
            case 2:
                if (!TryVarint(out ulong len) || len > (ulong)(_data.Length - _pos))
                {
                    return Fail();
                }

                payload = _data.Slice(_pos, (int)len);
                _pos += (int)len;
                return true;
            default:
                return Fail();
        }
    }

    private bool Fail()
    {
        _pos = _data.Length;
        return false;
    }

    private bool TryVarint(out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 70 && _pos < _data.Length; shift += 7)
        {
            byte b = _data[_pos++];
            if (shift < 64)
            {
                value |= (ulong)(b & 0x7F) << shift;
            }

            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
