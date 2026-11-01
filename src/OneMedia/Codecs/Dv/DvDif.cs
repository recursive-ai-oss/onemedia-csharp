using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Codecs.Dv;

/// <summary>Where in a DIF sequence a pack was found (IEC 61834-2 / SMPTE 314M: the block kind, from the section-type bits of the block identifier).</summary>
public enum DvPackSource
{
    /// <summary>A subcode block (time code, and on some cameras a second copy of the recording date and time).</summary>
    Subcode,

    /// <summary>A VAUX block (video source, source control, recording date and time, camera settings).</summary>
    Vaux,

    /// <summary>The first bytes of an audio block (audio source, source control, recording date and time).</summary>
    Aaux,
}

/// <summary>One 5-byte pack: a pack-type byte and four bytes of data.</summary>
/// <param name="Source">The kind of block that holds it.</param>
/// <param name="Type">The pack header byte (0x13 time code, 0x50 audio source, 0x60 video source, 0x62 recording date, 0x63 recording time…).</param>
/// <param name="Data">The four data bytes, first byte in the highest position.</param>
/// <param name="Offset">Offset of the pack's first byte from the start of the frame.</param>
public readonly record struct DvPack(DvPackSource Source, byte Type, uint Data, int Offset)
{
    /// <summary>One of the four data bytes (0 to 3).</summary>
    /// <param name="index">The byte index.</param>
    public byte this[int index] => (byte)(Data >> (24 - (8 * index)));
}

/// <summary>The kind of DV stream, from the data rate class.</summary>
public enum DvStreamClass
{
    /// <summary>25 Mbit/s: DV, DVCAM, DVCPRO 25 (one channel of DIF sequences).</summary>
    Dv25,

    /// <summary>50 Mbit/s: DVCPRO50 (two channels of DIF sequences).</summary>
    Dv50,

    /// <summary>100 Mbit/s: DVCPRO HD (four channels of DIF sequences).</summary>
    Dv100,
}

/// <summary>What the first DIF sequence of a DV frame says about the stream.</summary>
public sealed class DvFrameInfo
{
    internal DvFrameInfo()
    {
    }

    /// <summary>True for 625-line/50 Hz systems (the header's DSF bit); false for 525-line/60 Hz.</summary>
    public bool IsPal { get; internal set; }

    /// <summary>The data-rate class.</summary>
    public DvStreamClass StreamClass { get; internal set; }

    /// <summary>Number of DIF sequences in a frame: 10 or 12 per channel, times the number of channels.</summary>
    public int Sequences { get; internal set; }

    /// <summary>Frame size in bytes: 12,000 per DIF sequence.</summary>
    public int FrameSize => Sequences * DvDif.SequenceSize;

    /// <summary>Picture width in pixels.</summary>
    public int Width { get; internal set; }

    /// <summary>Picture height in pixels.</summary>
    public int Height { get; internal set; }

    /// <summary>Frame rate numerator (25, or 30000 for 525/60).</summary>
    public int FrameRateNumerator => IsPal ? 25 : 30000;

    /// <summary>Frame rate denominator (1, or 1001 for 525/60).</summary>
    public int FrameRateDenominator => IsPal ? 1 : 1001;

    /// <summary>Display aspect ratio width, from the video source control pack (4 or 16); 0 when the pack is absent.</summary>
    public int AspectWidth { get; internal set; }

    /// <summary>Display aspect ratio height (3 or 9); 0 when the pack is absent.</summary>
    public int AspectHeight { get; internal set; }

    /// <summary>Audio sampling rate from the audio source pack (48000, 44100 or 32000), 0 when the frame carries no audio source pack.</summary>
    public int AudioSampleRate { get; internal set; }

    /// <summary>Audio quantisation from the audio source pack: 16 (linear) or 12 (non-linear); 0 when unknown.</summary>
    public int AudioBits { get; internal set; }

    /// <summary>Number of audio channels the stream carries: 2 per audio block group, doubled in four-channel modes and for each extra DIF channel.</summary>
    public int AudioChannels { get; internal set; }

    /// <summary>The recording date and time (packs 0x62 and 0x63), in the camera's local clock; null when absent or not valid.</summary>
    public DateTime? RecordedAt { get; internal set; }

    /// <summary>The recording time code (pack 0x13) as <c>hh:mm:ss:ff</c>, or null.</summary>
    public string? Timecode { get; internal set; }

    /// <summary>Where the pack that gave <see cref="RecordedAt"/> lies, from the start of the frame; -1 when none.</summary>
    public int RecordedAtOffset { get; internal set; } = -1;

    /// <summary>Every pack found in the first DIF sequence, in order.</summary>
    public IReadOnlyList<DvPack> Packs { get; internal set; } = [];
}

/// <summary>
/// Reads the DIF structure of a DV frame (IEC 61834-2 and SMPTE 314M as publicly described; the field positions were checked against
/// files written by common tools). A frame is a set of DIF sequences of 150 blocks of 80 bytes. Each block starts with a 3-byte
/// identifier: section type (3 bits), channel/sequence numbers and the block number. A sequence is a header block, two subcode blocks,
/// three VAUX blocks, then nine groups of one audio block and fifteen video blocks.
/// </summary>
public static class DvDif
{
    /// <summary>Bytes in a DIF block.</summary>
    public const int BlockSize = 80;

    /// <summary>Blocks in a DIF sequence.</summary>
    public const int BlocksPerSequence = 150;

    /// <summary>Bytes in a DIF sequence.</summary>
    public const int SequenceSize = BlockSize * BlocksPerSequence;

    private const int SectionHeader = 0;
    private const int SectionSubcode = 1;
    private const int SectionVaux = 2;
    private const int SectionAudio = 3;

    /// <summary>
    /// Tells whether the bytes begin a DV frame: a header block (section type 0, sequence 0, block 0) followed, when there is room, by
    /// the two subcode blocks and three VAUX blocks in their places.
    /// </summary>
    /// <param name="data">The first bytes of the frame; at least 3.</param>
    /// <param name="strong">True when the blocks after the header also have the expected identifiers.</param>
    public static bool LooksLikeFrameStart(ReadOnlySpan<byte> data, out bool strong)
    {
        strong = false;
        // ID0: section type in bits 7..5 (0 = header); ID1: DIF sequence number in bits 7..4 (0), channel bit 3, then three reserved ones; ID2: block number.
        if (data.Length < 8 || (data[0] >> 5) != SectionHeader || (data[1] & 0xF0) != 0 || (data[1] & 0x07) != 0x07 || data[2] != 0)
        {
            return false;
        }

        if (data.Length >= 4 * BlockSize)
        {
            strong = (data[BlockSize] >> 5) == SectionSubcode && data[BlockSize + 2] == 0
                && (data[2 * BlockSize] >> 5) == SectionSubcode && data[(2 * BlockSize) + 2] == 1
                && (data[3 * BlockSize] >> 5) == SectionVaux && data[(3 * BlockSize) + 2] == 0;
        }

        return true;
    }

    /// <summary>
    /// Parses the stream parameters and packs from the start of a frame. Needs the first DIF sequence for the packs; with less
    /// (down to the first 8 bytes) only the header fields are filled.
    /// </summary>
    /// <param name="frame">The first bytes of a frame (a whole first sequence, 12,000 bytes, is best).</param>
    /// <param name="info">The parsed information.</param>
    /// <returns>False when the bytes are not the start of a DV frame.</returns>
    public static bool TryParseFrame(ReadOnlySpan<byte> frame, out DvFrameInfo info)
    {
        info = new DvFrameInfo();
        if (!LooksLikeFrameStart(frame, out _))
        {
            return false;
        }

        // Header block, byte 3 bit 7: DSF, 1 for the 625/50 system.
        info.IsPal = (frame[3] & 0x80) != 0;
        int perChannel = info.IsPal ? 12 : 10;
        List<DvPack> packs = [];
        int blocks = Math.Min(BlocksPerSequence, frame.Length / BlockSize);
        byte sourceType = 0xFF;
        byte controlByte2 = 0xFF;
        byte audioByte3 = 0xFF;
        byte audioByte4 = 0xFF;
        byte audioByte2 = 0xFF;
        byte dateDay = 0xFF;
        byte dateMonth = 0xFF;
        byte dateYear = 0xFF;
        byte timeSec = 0xFF;
        byte timeMin = 0xFF;
        byte timeHour = 0xFF;
        int dateOffset = -1;
        for (int b = 0; b < blocks; b++)
        {
            ReadOnlySpan<byte> block = frame.Slice(b * BlockSize, BlockSize);
            int section = block[0] >> 5;
            if (section == SectionSubcode)
            {
                // Six sync blocks of 8 bytes: two bytes of sync, one of identifier, then a 5-byte pack.
                for (int k = 0; k < 6; k++)
                {
                    AddPack(packs, DvPackSource.Subcode, block.Slice(3 + (k * 8) + 3, 5), (b * BlockSize) + 3 + (k * 8) + 3);
                }
            }
            else if (section == SectionVaux)
            {
                for (int k = 0; k < 15; k++)
                {
                    AddPack(packs, DvPackSource.Vaux, block.Slice(3 + (k * 5), 5), (b * BlockSize) + 3 + (k * 5));
                }
            }
            else if (section == SectionAudio)
            {
                AddPack(packs, DvPackSource.Aaux, block.Slice(3, 5), (b * BlockSize) + 3);
            }
        }

        foreach (DvPack p in packs)
        {
            switch (p.Type)
            {
                case 0x60 when p.Source == DvPackSource.Vaux && sourceType == 0xFF:
                    sourceType = (byte)(p[2] & 0x1F);
                    break;
                case 0x61 when p.Source == DvPackSource.Vaux && controlByte2 == 0xFF:
                    controlByte2 = p[1];
                    break;
                case 0x50 when p.Source == DvPackSource.Aaux && audioByte4 == 0xFF:
                    audioByte2 = p[1];
                    audioByte3 = p[2];
                    audioByte4 = p[3];
                    break;
                case 0x62 when p.Source == DvPackSource.Vaux && dateDay == 0xFF:
                    dateDay = p[1];
                    dateMonth = p[2];
                    dateYear = p[3];
                    dateOffset = p.Offset;
                    break;
                case 0x63 when p.Source == DvPackSource.Vaux && timeSec == 0xFF:
                    timeSec = p[1];
                    timeMin = p[2];
                    timeHour = p[3];
                    break;
                case 0x13 when p.Source == DvPackSource.Subcode && info.Timecode is null:
                    info.Timecode = Timecode(p);
                    break;
            }
        }

        info.Packs = packs;

        // Video source pack, data byte 3 (PC3) low five bits: 0 is the 25 Mbit/s system, 4 the 50 Mbit/s system; the HD systems have other values.
        // Observed in files written by common tools; the DVCPRO HD value for 1080i/50 is 0x14.
        int channels = sourceType switch { 0 or 1 => 1, 4 => 2, 0xFF => 1, _ => 4 };
        info.StreamClass = channels switch { 1 => DvStreamClass.Dv25, 2 => DvStreamClass.Dv50, _ => DvStreamClass.Dv100 };
        info.Sequences = perChannel * channels;
        if (channels == 4)
        {
            // DVCPRO HD: 1080i/50 is 1440x1080; 1080i/60 is 1280x1080.
            info.Width = info.IsPal ? 1440 : 1280;
            info.Height = 1080;
        }
        else
        {
            info.Width = 720;
            info.Height = info.IsPal ? 576 : 480;
        }

        // Video source control pack (0x61), second data byte, low three bits: display aspect (0 = 4:3, 2 = 16:9 full format, 7 = other).
        if (controlByte2 != 0xFF)
        {
            int disp = controlByte2 & 0x07;
            if (disp == 0)
            {
                info.AspectWidth = 4;
                info.AspectHeight = 3;
            }
            else if (disp is 2 or 3 or 6)
            {
                info.AspectWidth = 16;
                info.AspectHeight = 9;
            }
        }

        // Audio source pack (0x50), fourth data byte: bits 5..3 sampling frequency (0 = 48 kHz, 1 = 44.1 kHz, 2 = 32 kHz), bits 2..0 quantisation (0 = 16-bit linear, 1 = 12-bit non-linear).
        if (audioByte4 != 0xFF)
        {
            info.AudioSampleRate = ((audioByte4 >> 3) & 7) switch { 0 => 48000, 1 => 44100, 2 => 32000, _ => 0 };
            info.AudioBits = (audioByte4 & 7) switch { 0 => 16, 1 => 12, _ => 0 };
            // Third data byte, low five bits: audio mode group. 0 is two channels; a 12-bit 32 kHz stream carries four channels as two pairs.
            int pairs = (audioByte3 & 0x1F) == 2 || (info.AudioBits == 12 && info.AudioSampleRate == 32000) ? 2 : 1;
            info.AudioChannels = 2 * pairs * channels;
        }

        _ = audioByte2;
        if (dateDay != 0xFF && timeSec != 0xFF)
        {
            info.RecordedAt = ParseRecording(dateDay, dateMonth, dateYear, timeSec, timeMin, timeHour);
            info.RecordedAtOffset = info.RecordedAt is null ? -1 : dateOffset;
        }

        return true;
    }

    private static void AddPack(List<DvPack> into, DvPackSource source, ReadOnlySpan<byte> five, int offset)
    {
        // A slot that holds 0xFF is empty ("no information").
        if (five[0] == 0xFF)
        {
            return;
        }

        into.Add(new DvPack(source, five[0], BinaryPrimitives.ReadUInt32BigEndian(five[1..]), offset));
    }

    private static string Timecode(DvPack p)
    {
        // Time code pack: frames (BCD, bit 6 drop-frame), seconds, minutes, hours (BCD, top bits flags).
        string sep = (p[0] & 0x40) != 0 ? ";" : ":";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Bcd(p[3] & 0x3F):00}:{Bcd(p[2] & 0x7F):00}:{Bcd(p[1] & 0x7F):00}{sep}{Bcd(p[0] & 0x3F):00}");
    }

    private static int Bcd(int v) => ((v >> 4) * 10) + (v & 0x0F);

    private static bool ValidBcd(int v) => (v >> 4) <= 9 && (v & 0x0F) <= 9;

    // Recording date pack: day (bits 5..0), month (bits 4..0), year (BCD, two digits); recording time pack: seconds, minutes, hours.
    // Two-digit years from 70 on are 19xx (digital video began in the 1990s).
    private static DateTime? ParseRecording(byte day, byte month, byte year, byte sec, byte min, byte hour)
    {
        int d = day & 0x3F;
        int mo = month & 0x1F;
        int y = year;
        int s = sec & 0x7F;
        int mi = min & 0x7F;
        int h = hour & 0x3F;
        if (!ValidBcd(d) || !ValidBcd(mo) || !ValidBcd(y) || !ValidBcd(s) || !ValidBcd(mi) || !ValidBcd(h))
        {
            return null;
        }

        d = Bcd(d);
        mo = Bcd(mo);
        y = Bcd(y);
        s = Bcd(s);
        mi = Bcd(mi);
        h = Bcd(h);
        int fullYear = y >= 70 ? 1900 + y : 2000 + y;
        // Cameras with an unset clock write a date in 1970 or earlier; digital video began in 1995, so such dates are not recording dates.
        if (fullYear < 1995 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(fullYear, mo) || h > 23 || mi > 59 || s > 59)
        {
            return null;
        }

        return new DateTime(fullYear, mo, d, h, mi, s, DateTimeKind.Unspecified);
    }
}
