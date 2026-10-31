namespace RecursiveAI.OneMedia.Codecs;

/// <summary>
/// Iterates the messages of an SEI RBSP (ITU-T H.264 §7.3.2.3.1, H.265 §7.3.2.4, H.266 §7.3.2.5): payload type and size, then the
/// payload bytes. It only frames messages; payload contents (captions, timing and so on) are interpreted elsewhere.
/// </summary>
public ref struct SeiMessageReader
{
    private readonly ReadOnlySpan<byte> _rbsp;
    private int _pos;

    /// <summary>Creates a reader.</summary>
    /// <param name="rbsp">The SEI NAL unit's payload without its header and with emulation-prevention bytes removed
    /// (see <see cref="EmulationPrevention.Remove"/>), since payload sizes count unescaped bytes.</param>
    public SeiMessageReader(ReadOnlySpan<byte> rbsp)
    {
        _rbsp = rbsp;
        _pos = 0;
    }

    /// <summary>Reads the next message.</summary>
    /// <param name="payloadType">The payload type.</param>
    /// <param name="payload">The payload bytes, a slice of the input.</param>
    /// <returns>False at the end of the messages, at the RBSP trailing bits, or when a size runs past the data (the remainder is then ignored).</returns>
    public bool TryRead(out int payloadType, out ReadOnlySpan<byte> payload)
    {
        payloadType = 0;
        payload = default;
        // more_rbsp_data(): a lone 0x80 byte is the trailing bits.
        if (_pos >= _rbsp.Length || (_pos == _rbsp.Length - 1 && _rbsp[_pos] == 0x80))
        {
            return false;
        }

        long type = 0;
        while (_pos < _rbsp.Length && _rbsp[_pos] == 0xff)
        {
            type += 255;
            _pos++;
        }

        if (_pos >= _rbsp.Length)
        {
            return false;
        }

        type += _rbsp[_pos++];
        long size = 0;
        while (_pos < _rbsp.Length && _rbsp[_pos] == 0xff)
        {
            size += 255;
            _pos++;
        }

        if (_pos >= _rbsp.Length)
        {
            return false;
        }

        size += _rbsp[_pos++];
        if (size > _rbsp.Length - _pos || type > int.MaxValue)
        {
            _pos = _rbsp.Length;
            return false;
        }

        payloadType = (int)type;
        payload = _rbsp.Slice(_pos, (int)size);
        _pos += (int)size;
        return true;
    }
}
