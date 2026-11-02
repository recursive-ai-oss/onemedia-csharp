namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>Reads the PES_data_field of a teletext elementary stream (ETSI EN 300 472 §4.3).</summary>
public static class TeletextPes
{
    /// <summary>
    /// Pushes every EBU teletext data unit of one PES payload into <paramref name="decoder"/>.
    /// </summary>
    /// <param name="payload">The PES packet data bytes: the data_identifier followed by data units.</param>
    /// <param name="decoder">Receives the teletext packets.</param>
    /// <param name="time">The presentation time of the PES packet; every unit in it carries this time.</param>
    /// <param name="offset">Offset of the PES packet in the source, for defect reports, or -1.</param>
    /// <param name="defects">Where malformed units are reported, or null.</param>
    /// <returns>The number of teletext packets pushed.</returns>
    public static int Push(ReadOnlySpan<byte> payload, TeletextPageDecoder decoder, MediaTime time, long offset = -1, DefectLog? defects = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (payload.IsEmpty)
        {
            return 0;
        }

        // Table 3: data_identifier 0x10 to 0x1F is EBU data. Streams in the wild also use 0x00 to 0x0F or later values; the data units
        // are self-describing, so other identifiers are read the same way but reported.
        if (payload[0] is < 0x10 or > 0x1F)
        {
            defects?.Add(DefectKind.ReservedValue, DefectSeverity.Info, offset, "Teletext PES data_identifier is not in the EBU range 0x10 to 0x1F.", "teletext.data-identifier");
        }

        Span<byte> unit = stackalloc byte[44]; // one data unit's framing code, address and data block, bit-reversed
        int pos = 1;
        int pushed = 0;
        while (pos + 2 <= payload.Length)
        {
            int id = payload[pos];
            int length = payload[pos + 1];
            pos += 2;
            if (pos + length > payload.Length)
            {
                defects?.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, "Teletext data unit runs past the end of its PES packet.", "teletext.unit-truncated");
                break;
            }

            // Table 4: 0x02 is non-subtitle EBU teletext, 0x03 subtitle data, 0xFF stuffing.
            if (id is 0x02 or 0x03 && length == 0x2C)
            {
                // Table 2: byte 0 holds field_parity and line_offset, then framing_code, the 2 address bytes and 40 data bytes.
                for (int i = 0; i < 43; i++)
                {
                    unit[i] = TeletextCodes.ReverseBits(payload[pos + 1 + i]);
                }

                int a = TeletextCodes.DecodeHamming84(unit[1]);
                int b2 = TeletextCodes.DecodeHamming84(unit[2]);
                if ((a | b2) < 0)
                {
                    decoder.CountError();
                    defects?.Add(DefectKind.InvalidValue, DefectSeverity.Info, offset, "Teletext packet address has an uncorrectable Hamming 8/4 byte; the packet is skipped.", "teletext.address-hamming");
                }
                else
                {
                    // ETS 300 706 §7.1.2: magazine = address bits 1-3, packet number = bit 4 plus the second byte's four bits.
                    int magazine = a & 7;
                    int row = ((a >> 3) & 1) | (b2 << 1);
                    decoder.Push(magazine, row, unit.Slice(3, 40), time, offset);
                    pushed++;
                }
            }
            else if (id != 0xFF && id is not (0x02 or 0x03))
            {
                defects?.Add(DefectKind.ReservedValue, DefectSeverity.Info, offset, "Teletext PES holds a data unit of another type; it is skipped.", "teletext.unit-id");
            }

            pos += length;
        }

        return pushed;
    }
}
