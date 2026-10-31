using System.Buffers;
using System.Globalization;

namespace RecursiveAI.OneMedia.Codecs.Vpx;

/// <summary>The fields of a VP8 key frame header (RFC 6386 §9.1).</summary>
/// <param name="Version">The 3-bit version (profile) from the frame tag.</param>
/// <param name="ShowFrame">show_frame.</param>
/// <param name="Width">Horizontal size in pixels (14 bits).</param>
/// <param name="Height">Vertical size in pixels (14 bits).</param>
/// <param name="HorizontalScale">Upscaling code for the width (2 bits).</param>
/// <param name="VerticalScale">Upscaling code for the height (2 bits).</param>
public readonly record struct Vp8KeyFrameHeader(int Version, bool ShowFrame, int Width, int Height, int HorizontalScale, int VerticalScale)
{
    /// <summary>Tells whether a frame is a key frame without parsing more.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>True when the first bit of the frame tag is 0.</returns>
    public static bool IsKeyFrame(ReadOnlySpan<byte> frame) => frame.Length >= 3 && (frame[0] & 1) == 0;

    /// <summary>Parses a key frame header.</summary>
    /// <param name="frame">The frame, starting with the 3-byte frame tag.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False for inter frames, bad start codes or truncated data.</returns>
    public static bool TryParse(ReadOnlySpan<byte> frame, out Vp8KeyFrameHeader header)
    {
        header = default;
        if (frame.Length < 10 || (frame[0] & 1) != 0 || frame[3] != 0x9d || frame[4] != 0x01 || frame[5] != 0x2a)
        {
            return false;
        }

        int w = frame[6] | (frame[7] << 8);
        int h = frame[8] | (frame[9] << 8);
        header = new Vp8KeyFrameHeader((frame[0] >> 1) & 7, (frame[0] & 0x10) != 0, w & 0x3fff, h & 0x3fff, w >> 14, h >> 14);
        return header.Width != 0 && header.Height != 0;
    }
}

/// <summary>The fields of a VP9 uncompressed header that describe the stream (VP9 bitstream specification §6.2).</summary>
public readonly record struct Vp9FrameHeader
{
    /// <summary>profile, 0 to 3.</summary>
    public int Profile { get; init; }

    /// <summary>True when show_existing_frame is set; no other field except <see cref="FrameToShow"/> is then valid.</summary>
    public bool ShowExistingFrame { get; init; }

    /// <summary>frame_to_show_map_idx.</summary>
    public int FrameToShow { get; init; }

    /// <summary>True for a key frame (frame_type 0).</summary>
    public bool KeyFrame { get; init; }

    /// <summary>show_frame.</summary>
    public bool ShowFrame { get; init; }

    /// <summary>intra_only.</summary>
    public bool IntraOnly { get; init; }

    /// <summary>True when the header carries colour configuration and frame size (key frames and intra-only frames).</summary>
    public bool HasSize { get; init; }

    /// <summary>BitDepth: 8, 10 or 12.</summary>
    public int BitDepth { get; init; }

    /// <summary>color_space code (§7.2.2): 0 unknown, 1 BT.601, 2 BT.709, 3 SMPTE 170M, 4 SMPTE 240M, 5 BT.2020, 6 reserved, 7 sRGB.</summary>
    public int ColorSpace { get; init; }

    /// <summary>color_range: true for full range.</summary>
    public bool FullRange { get; init; }

    /// <summary>subsampling_x.</summary>
    public bool SubsamplingX { get; init; }

    /// <summary>subsampling_y.</summary>
    public bool SubsamplingY { get; init; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>The chroma format implied by the subsampling flags.</summary>
    public ChromaFormat Chroma => SubsamplingX && SubsamplingY ? ChromaFormat.Yuv420 : SubsamplingX ? ChromaFormat.Yuv422 : SubsamplingY ? ChromaFormat.Unknown : ChromaFormat.Yuv444;

    /// <summary>Parses the uncompressed header at the start of a frame.</summary>
    /// <param name="frame">One frame (not a superframe).</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when truncated, the marker is wrong, or a sync code is missing. Never throws, never allocates.</returns>
    public static bool TryParse(ReadOnlySpan<byte> frame, out Vp9FrameHeader header)
    {
        header = default;
        BitReader r = new(frame);
        if (r.ReadBits(2) != 2)
        {
            return false;
        }

        int profile = r.ReadBit();
        profile |= r.ReadBit() << 1;
        if (profile == 3 && r.ReadFlag())
        {
            return false; // reserved_zero
        }

        if (r.ReadFlag())
        {
            header = new Vp9FrameHeader { Profile = profile, ShowExistingFrame = true, FrameToShow = (int)r.ReadBits(3) };
            return !r.Overrun;
        }

        bool key = r.ReadBit() == 0;
        bool show = r.ReadFlag();
        bool errorRes = r.ReadFlag();
        bool intraOnly = false;
        Vp9FrameHeader h = new() { Profile = profile, KeyFrame = key, ShowFrame = show };
        if (key)
        {
            if (!ReadSyncCode(ref r))
            {
                return false;
            }

            h = ReadColorConfig(ref r, h);
            h = h with { Width = (int)r.ReadBits(16) + 1, Height = (int)r.ReadBits(16) + 1, HasSize = true };
        }
        else
        {
            intraOnly = !show && r.ReadFlag();
            if (!errorRes)
            {
                r.SkipBits(2); // reset_frame_context
            }

            if (intraOnly)
            {
                if (!ReadSyncCode(ref r))
                {
                    return false;
                }

                // §6.2: profile 0 intra-only frames use 8-bit 4:2:0 BT.601 without a colour_config
                h = profile > 0 ? ReadColorConfig(ref r, h) : h with { BitDepth = 8, ColorSpace = 1, SubsamplingX = true, SubsamplingY = true };
                r.SkipBits(8); // refresh_frame_flags
                h = h with { Width = (int)r.ReadBits(16) + 1, Height = (int)r.ReadBits(16) + 1, HasSize = true, IntraOnly = true };
            }
        }

        if (r.Overrun)
        {
            return false;
        }

        header = h;
        return true;
    }

    private static bool ReadSyncCode(ref BitReader r) => r.ReadBits(8) == 0x49 && r.ReadBits(8) == 0x83 && r.ReadBits(8) == 0x42;

    private static Vp9FrameHeader ReadColorConfig(ref BitReader r, Vp9FrameHeader h)
    {
        int bitDepth = 8;
        if (h.Profile >= 2)
        {
            bitDepth = r.ReadFlag() ? 12 : 10;
        }

        int cs = (int)r.ReadBits(3);
        bool full, sx, sy;
        if (cs != 7)
        {
            full = r.ReadFlag();
            if (h.Profile is 1 or 3)
            {
                sx = r.ReadFlag();
                sy = r.ReadFlag();
                r.ReadBit(); // reserved_zero
            }
            else
            {
                sx = sy = true;
            }
        }
        else
        {
            full = true;
            sx = sy = false;
            if (h.Profile is 1 or 3)
            {
                r.ReadBit(); // reserved_zero
            }
        }

        return h with { BitDepth = bitDepth, ColorSpace = cs, FullRange = full, SubsamplingX = sx, SubsamplingY = sy };
    }
}

/// <summary>The superframe index of a VP9 packet (VP9 bitstream specification Annex B).</summary>
public static class Vp9Superframe
{
    /// <summary>Largest number of frames a superframe holds.</summary>
    public const int MaxFrames = 8;

    /// <summary>Reads a superframe index from the end of a packet.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="frameSizes">Receives the frame sizes; must hold at least <see cref="MaxFrames"/> entries to receive them all.</param>
    /// <param name="frameCount">The number of frames found (up to the length of <paramref name="frameSizes"/>); 1 and nothing written for a packet without an index.</param>
    /// <returns>True when an index was found and is consistent with the packet size (frame sizes sum to the data before the index).</returns>
    public static bool TryRead(ReadOnlySpan<byte> packet, Span<int> frameSizes, out int frameCount)
    {
        frameCount = 1;
        if (packet.Length == 0)
        {
            return false;
        }

        int marker = packet[^1];
        if ((marker & 0xe0) != 0xc0)
        {
            return false;
        }

        int frames = (marker & 7) + 1;
        int bytes = ((marker >> 3) & 3) + 1;
        int indexSize = 2 + (bytes * frames);
        if (packet.Length < indexSize || packet[^indexSize] != marker)
        {
            return false;
        }

        long total = 0;
        int pos = packet.Length - indexSize + 1;
        for (int i = 0; i < frames; i++)
        {
            long size = 0;
            for (int b = 0; b < bytes; b++)
            {
                size |= (long)packet[pos++] << (8 * b);
            }

            total += size;
            if (i < frameSizes.Length)
            {
                frameSizes[i] = (int)Math.Min(size, int.MaxValue);
            }
        }

        if (total > packet.Length - indexSize)
        {
            return false;
        }

        frameCount = Math.Min(frames, frameSizes.Length);
        return true;
    }
}

/// <summary>
/// A VPCodecConfigurationRecord, the content of a <c>vpcC</c> box (VP Codec ISO Media File Format Binding, v1.0, version 1 full box).
/// </summary>
public sealed class VpCodecConfigurationRecord
{
    /// <summary>Profile.</summary>
    public int Profile { get; init; }

    /// <summary>Level, times ten (for example 31 for level 3.1).</summary>
    public int Level { get; init; }

    /// <summary>Bit depth.</summary>
    public int BitDepth { get; init; }

    /// <summary>chromaSubsampling: 0 4:2:0 vertical, 1 4:2:0 co-located, 2 4:2:2, 3 4:4:4.</summary>
    public int ChromaSubsampling { get; init; }

    /// <summary>videoFullRangeFlag.</summary>
    public bool FullRange { get; init; }

    /// <summary>colourPrimaries (H.273).</summary>
    public int ColourPrimaries { get; init; } = 2;

    /// <summary>transferCharacteristics (H.273).</summary>
    public int TransferCharacteristics { get; init; } = 2;

    /// <summary>matrixCoefficients (H.273).</summary>
    public int MatrixCoefficients { get; init; } = 2;

    /// <summary>Parses the box content, version and flags included.</summary>
    /// <param name="data">The content after the box header.</param>
    /// <param name="record">The result when true.</param>
    /// <returns>False when truncated or the version is not 1.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out VpCodecConfigurationRecord? record)
    {
        record = null;
        if (data.Length < 12 || data[0] != 1)
        {
            return false;
        }

        record = new VpCodecConfigurationRecord
        {
            Profile = data[4],
            Level = data[5],
            BitDepth = data[6] >> 4,
            ChromaSubsampling = (data[6] >> 1) & 7,
            FullRange = (data[6] & 1) != 0,
            ColourPrimaries = data[7],
            TransferCharacteristics = data[8],
            MatrixCoefficients = data[9],
        };
        return true;
    }

    /// <summary>The size <see cref="Write"/> produces.</summary>
    public const int Size = 12;

    /// <summary>Writes the record as a version 1 full box payload with an empty codecInitializationData.</summary>
    /// <param name="writer">Receives <see cref="Size"/> bytes.</param>
    public void Write(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(Size);
        w[0] = 1;
        w[1] = w[2] = w[3] = 0;
        w[4] = (byte)Profile;
        w[5] = (byte)Level;
        w[6] = (byte)((BitDepth << 4) | ((ChromaSubsampling & 7) << 1) | (FullRange ? 1 : 0));
        w[7] = (byte)ColourPrimaries;
        w[8] = (byte)TransferCharacteristics;
        w[9] = (byte)MatrixCoefficients;
        w[10] = w[11] = 0;
        writer.Advance(Size);
    }

    /// <summary>Builds the record from a key frame header.</summary>
    /// <param name="h">A parsed key frame or intra-only header.</param>
    /// <param name="level">The level times ten; 0 when unknown.</param>
    /// <returns>The record. The colour codes map from the VP9 color_space (§7.2.2) to H.273 values.</returns>
    public static VpCodecConfigurationRecord FromFrameHeader(in Vp9FrameHeader h, int level = 0)
    {
        (int p, int t, int m) = h.ColorSpace switch
        {
            1 => (6, 6, 6),   // BT.601
            2 => (1, 1, 1),   // BT.709
            3 => (6, 6, 6),   // SMPTE 170M
            4 => (7, 7, 7),   // SMPTE 240M
            5 => (9, 14, 9),  // BT.2020
            7 => (1, 13, 0),  // sRGB
            _ => (2, 2, 2),
        };
        return new VpCodecConfigurationRecord
        {
            Profile = h.Profile,
            Level = level,
            BitDepth = h.BitDepth,
            ChromaSubsampling = h.SubsamplingX && h.SubsamplingY ? 1 : h.SubsamplingX ? 2 : 3,
            FullRange = h.FullRange,
            ColourPrimaries = p,
            TransferCharacteristics = t,
            MatrixCoefficients = m,
        };
    }

    /// <summary>The <c>vp09.PP.LL.DD</c> string (VP Codec ISO Media File Format Binding §5), with the optional colour fields appended.</summary>
    /// <param name="includeColor">True to add chroma subsampling, colour codes and range after the bit depth.</param>
    /// <returns>The string.</returns>
    public string ToRfc6381(bool includeColor = false)
    {
        string s = string.Create(CultureInfo.InvariantCulture, $"vp09.{Profile:D2}.{Level:D2}.{BitDepth:D2}");
        return includeColor
            ? string.Create(CultureInfo.InvariantCulture, $"{s}.{ChromaSubsampling:D2}.{ColourPrimaries:D2}.{TransferCharacteristics:D2}.{MatrixCoefficients:D2}.{(FullRange ? 1 : 0):D2}")
            : s;
    }

    /// <summary>Describes the stream from the record.</summary>
    /// <returns>The summary.</returns>
    public CodecParameters Describe() => new()
    {
        Codec = CodecId.Vp9,
        Rfc6381 = ToRfc6381(),
        Profile = Profile,
        Level = Level,
        BitDepth = BitDepth,
        Chroma = ChromaSubsampling switch { 0 or 1 => ChromaFormat.Yuv420, 2 => ChromaFormat.Yuv422, 3 => ChromaFormat.Yuv444, _ => ChromaFormat.Unknown },
        Color = new ColorInfo(true, ColourPrimaries, TransferCharacteristics, MatrixCoefficients, FullRange),
    };
}

/// <summary>
/// The VP9 CodecPrivate of WebM (WebM container guidelines, "VP9 Codec Feature Metadata"): a list of
/// (id, length, value) entries for profile, level, bit depth and chroma subsampling.
/// </summary>
public static class Vp9CodecPrivate
{
    /// <summary>Reads the feature list.</summary>
    /// <param name="data">The CodecPrivate bytes.</param>
    /// <param name="profile">Profile (id 1), -1 when absent.</param>
    /// <param name="level">Level (id 2), -1 when absent.</param>
    /// <param name="bitDepth">Bit depth (id 3), -1 when absent.</param>
    /// <param name="chromaSubsampling">Chroma subsampling (id 4), -1 when absent.</param>
    /// <returns>False when an entry is truncated.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out int profile, out int level, out int bitDepth, out int chromaSubsampling)
    {
        profile = level = bitDepth = chromaSubsampling = -1;
        int pos = 0;
        while (pos < data.Length)
        {
            if (pos + 2 > data.Length || pos + 2 + data[pos + 1] > data.Length)
            {
                return false;
            }

            int id = data[pos];
            int len = data[pos + 1];
            int value = len >= 1 ? data[pos + 2] : -1;
            switch (id)
            {
                case 1: profile = value; break;
                case 2: level = value; break;
                case 3: bitDepth = value; break;
                case 4: chromaSubsampling = value; break;
            }

            pos += 2 + len;
        }

        return true;
    }

    /// <summary>Writes the four entries.</summary>
    /// <param name="writer">Receives 12 bytes.</param>
    /// <param name="profile">Profile.</param>
    /// <param name="level">Level.</param>
    /// <param name="bitDepth">Bit depth.</param>
    /// <param name="chromaSubsampling">Chroma subsampling code as in <c>vpcC</c>.</param>
    public static void Write(IBufferWriter<byte> writer, int profile, int level, int bitDepth, int chromaSubsampling)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(12);
        w[0] = 1; w[1] = 1; w[2] = (byte)profile;
        w[3] = 2; w[4] = 1; w[5] = (byte)level;
        w[6] = 3; w[7] = 1; w[8] = (byte)bitDepth;
        w[9] = 4; w[10] = 1; w[11] = (byte)chromaSubsampling;
        writer.Advance(12);
    }
}
