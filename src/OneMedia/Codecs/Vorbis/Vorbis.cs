using System.Buffers;

namespace RecursiveAI.OneMedia.Codecs.Vorbis;

/// <summary>The Vorbis identification header (Vorbis I specification §4.2.2).</summary>
/// <param name="Channels">audio_channels.</param>
/// <param name="SampleRate">audio_sample_rate in Hz.</param>
/// <param name="BitrateMaximum">bitrate_maximum in bits per second, 0 or negative when unset.</param>
/// <param name="BitrateNominal">bitrate_nominal.</param>
/// <param name="BitrateMinimum">bitrate_minimum.</param>
/// <param name="BlockSize0">The short block size in samples (2 to the power of blocksize_0).</param>
/// <param name="BlockSize1">The long block size in samples.</param>
public readonly record struct VorbisIdentificationHeader(int Channels, int SampleRate, int BitrateMaximum, int BitrateNominal, int BitrateMinimum, int BlockSize0, int BlockSize1)
{
    /// <summary>The header packet type for identification headers.</summary>
    public const int PacketTypeIdentification = 1;

    /// <summary>The header packet type for comment headers.</summary>
    public const int PacketTypeComment = 3;

    /// <summary>The header packet type for setup headers.</summary>
    public const int PacketTypeSetup = 5;

    /// <summary>Tells which header, if any, a packet is.</summary>
    /// <param name="packet">A packet.</param>
    /// <returns>1, 3 or 5 for identification, comment or setup headers (odd first byte followed by <c>vorbis</c>), otherwise 0.</returns>
    public static int GetHeaderType(ReadOnlySpan<byte> packet) =>
        packet.Length >= 7 && (packet[0] & 1) == 1 && packet.Slice(1, 6).SequenceEqual("vorbis"u8) && packet[0] is 1 or 3 or 5 ? packet[0] : 0;

    /// <summary>Parses the identification header packet.</summary>
    /// <param name="packet">The packet including the 7-byte prefix.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when the prefix, version, channel count, rate, block sizes or framing bit are invalid.</returns>
    public static bool TryParse(ReadOnlySpan<byte> packet, out VorbisIdentificationHeader header)
    {
        header = default;
        if (packet.Length < 30 || GetHeaderType(packet) != PacketTypeIdentification)
        {
            return false;
        }

        BitReader r = new(packet[7..]);
        // All multi-byte fields are little-endian (§2.1.4), unlike the bit reader's order.
        uint version = ReadLe32(ref r);
        int channels = (int)r.ReadBits(8);
        uint rate = ReadLe32(ref r);
        int max = (int)ReadLe32(ref r);
        int nominal = (int)ReadLe32(ref r);
        int min = (int)ReadLe32(ref r);
        int b = (int)r.ReadBits(8);
        int bs0 = b & 0xf;
        int bs1 = b >> 4;
        bool framing = (packet[29] & 1) != 0;
        if (version != 0 || channels == 0 || rate == 0 || rate > int.MaxValue || bs0 < 6 || bs1 > 13 || bs0 > bs1 || !framing)
        {
            return false;
        }

        header = new VorbisIdentificationHeader(channels, (int)rate, max, nominal, min, 1 << bs0, 1 << bs1);
        return true;
    }

    private static uint ReadLe32(ref BitReader r)
    {
        uint v = r.ReadBits(8);
        v |= r.ReadBits(8) << 8;
        v |= r.ReadBits(8) << 16;
        v |= r.ReadBits(8) << 24;
        return v;
    }

    /// <summary>Describes the stream.</summary>
    /// <returns>The summary.</returns>
    public CodecParameters Describe() => new()
    {
        Codec = CodecId.Vorbis,
        Rfc6381 = "vorbis",
        SampleRate = SampleRate,
        Channels = Channels,
        BitRate = BitrateNominal > 0 ? BitrateNominal : 0,
    };
}

/// <summary>The three Vorbis header packets as Matroska stores them in CodecPrivate: Xiph lacing (Matroska codec specification, Vorbis).</summary>
public static class VorbisXiphLacing
{
    /// <summary>Splits a CodecPrivate into its identification, comment and setup packets.</summary>
    /// <param name="codecPrivate">The CodecPrivate bytes.</param>
    /// <param name="identification">The identification packet, a slice of the input.</param>
    /// <param name="comment">The comment packet.</param>
    /// <param name="setup">The setup packet.</param>
    /// <returns>False when the lacing is malformed (the first byte must be 2).</returns>
    public static bool TrySplit(ReadOnlyMemory<byte> codecPrivate, out ReadOnlyMemory<byte> identification, out ReadOnlyMemory<byte> comment, out ReadOnlyMemory<byte> setup)
    {
        identification = comment = setup = default;
        ReadOnlySpan<byte> s = codecPrivate.Span;
        if (s.Length < 3 || s[0] != 2)
        {
            return false;
        }

        int pos = 1;
        long first = 0, second = 0;
        while (pos < s.Length && s[pos] == 255)
        {
            first += 255;
            pos++;
        }

        if (pos >= s.Length)
        {
            return false;
        }

        first += s[pos++];
        while (pos < s.Length && s[pos] == 255)
        {
            second += 255;
            pos++;
        }

        if (pos >= s.Length)
        {
            return false;
        }

        second += s[pos++];
        if (first + second > s.Length - pos)
        {
            return false;
        }

        identification = codecPrivate.Slice(pos, (int)first);
        comment = codecPrivate.Slice(pos + (int)first, (int)second);
        setup = codecPrivate[(pos + (int)first + (int)second)..];
        return true;
    }

    /// <summary>The size of the laced CodecPrivate.</summary>
    /// <param name="identificationLength">Identification packet length.</param>
    /// <param name="commentLength">Comment packet length.</param>
    /// <param name="setupLength">Setup packet length.</param>
    /// <returns>Bytes.</returns>
    public static int GetSize(int identificationLength, int commentLength, int setupLength) =>
        1 + (identificationLength / 255) + 1 + (commentLength / 255) + 1 + identificationLength + commentLength + setupLength;

    /// <summary>Builds a CodecPrivate from the three header packets.</summary>
    /// <param name="identification">The identification packet.</param>
    /// <param name="comment">The comment packet.</param>
    /// <param name="setup">The setup packet.</param>
    /// <param name="writer">Receives <see cref="GetSize"/> bytes.</param>
    public static void Build(ReadOnlySpan<byte> identification, ReadOnlySpan<byte> comment, ReadOnlySpan<byte> setup, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int total = GetSize(identification.Length, comment.Length, setup.Length);
        Span<byte> w = writer.GetSpan(total);
        int p = 0;
        w[p++] = 2;
        p = PutLace(w, p, identification.Length);
        p = PutLace(w, p, comment.Length);
        identification.CopyTo(w[p..]);
        p += identification.Length;
        comment.CopyTo(w[p..]);
        p += comment.Length;
        setup.CopyTo(w[p..]);
        p += setup.Length;
        writer.Advance(p);
    }

    private static int PutLace(Span<byte> w, int p, int length)
    {
        for (int i = 0; i < length / 255; i++)
        {
            w[p++] = 255;
        }

        w[p++] = (byte)(length % 255);
        return p;
    }
}

/// <summary>
/// The mode table of a Vorbis setup header, enough to tell each audio packet's block size and so its duration (Vorbis I
/// specification §4.2.4 and §4.3.1). Nothing is decoded: the setup header is walked only to reach the modes.
/// </summary>
public sealed class VorbisModes
{
    private readonly bool[] _longBlock;

    private VorbisModes(bool[] longBlock, int shortBlock, int longSize)
    {
        _longBlock = longBlock;
        ShortBlockSize = shortBlock;
        LongBlockSize = longSize;
    }

    /// <summary>The short block size in samples.</summary>
    public int ShortBlockSize { get; }

    /// <summary>The long block size in samples.</summary>
    public int LongBlockSize { get; }

    /// <summary>The number of modes.</summary>
    public int ModeCount => _longBlock.Length;

    /// <summary>Reads the modes from a setup header.</summary>
    /// <param name="setup">The setup header packet, including its 7-byte prefix.</param>
    /// <param name="identification">The matching identification header.</param>
    /// <param name="modes">The result when true.</param>
    /// <returns>False when the setup header is malformed or truncated. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> setup, in VorbisIdentificationHeader identification, out VorbisModes? modes)
    {
        modes = null;
        if (VorbisIdentificationHeader.GetHeaderType(setup) != VorbisIdentificationHeader.PacketTypeSetup)
        {
            return false;
        }

        // Vorbis packs bits least significant first; the helper below reads that way.
        LsbReader b = new(setup[7..]);
        int channels = identification.Channels;

        // §4.2.4.1 codebooks
        int books = b.Read(8) + 1;
        for (int i = 0; i < books; i++)
        {
            if (b.Read(24) != 0x564342)
            {
                return false;
            }

            int dims = b.Read(16);
            long entries = b.Read(24);
            bool ordered = b.Read(1) == 1;
            if (!ordered)
            {
                bool sparse = b.Read(1) == 1;
                for (long e = 0; e < entries; e++)
                {
                    if (sparse)
                    {
                        if (b.Read(1) == 1)
                        {
                            b.Read(5);
                        }
                    }
                    else
                    {
                        b.Read(5);
                    }

                    if (b.Overrun)
                    {
                        return false;
                    }
                }
            }
            else
            {
                b.Read(5);
                long current = 0;
                while (current < entries)
                {
                    int number = b.Read(ILog((int)(entries - current)));
                    current += number;
                    // A run of zero entries would never end, so it marks a corrupt table.
                    if (b.Overrun || number <= 0)
                    {
                        return false;
                    }
                }
            }

            int lookup = b.Read(4);
            if (lookup is 1 or 2)
            {
                b.Read(32);
                b.Read(32);
                int valueBits = b.Read(4) + 1;
                b.Read(1);
                long values = lookup == 1 ? Lookup1Values(entries, dims) : entries * dims;
                if (values < 0 || values > 1 << 24)
                {
                    return false;
                }

                b.Skip(values * valueBits);
            }
            else if (lookup != 0)
            {
                return false;
            }

            if (b.Overrun)
            {
                return false;
            }
        }

        // §4.2.4.2 time domain transforms
        int times = b.Read(6) + 1;
        for (int i = 0; i < times; i++)
        {
            if (b.Read(16) != 0)
            {
                return false;
            }
        }

        // §4.2.4.3 floors
        int floors = b.Read(6) + 1;
        Span<int> classDimensions = stackalloc int[16]; // fixed: at most 16 classes (4-bit class numbers)
        Span<int> partitionClass = stackalloc int[32]; // fixed: at most 31 partitions (5-bit count)
        for (int i = 0; i < floors; i++)
        {
            int type = b.Read(16);
            if (type == 0)
            {
                b.Skip(8 + 16 + 16 + 6 + 8);
                int bookCount = b.Read(4) + 1;
                b.Skip(8L * bookCount);
            }
            else if (type == 1)
            {
                int partitions = b.Read(5);
                int maxClass = -1;
                for (int p = 0; p < partitions; p++)
                {
                    partitionClass[p] = b.Read(4);
                    maxClass = Math.Max(maxClass, partitionClass[p]);
                }

                for (int c = 0; c <= maxClass; c++)
                {
                    classDimensions[c] = b.Read(3) + 1;
                    int subclasses = b.Read(2);
                    if (subclasses != 0)
                    {
                        b.Read(8);
                    }

                    b.Skip(8L << subclasses);
                }

                b.Skip(2);
                int rangeBits = b.Read(4);
                for (int p = 0; p < partitions; p++)
                {
                    b.Skip((long)classDimensions[partitionClass[p]] * rangeBits);
                }
            }
            else
            {
                return false;
            }

            if (b.Overrun)
            {
                return false;
            }
        }

        // §4.2.4.4 residues
        int residues = b.Read(6) + 1;
        for (int i = 0; i < residues; i++)
        {
            if (b.Read(16) > 2)
            {
                return false;
            }

            b.Skip(24 + 24 + 24);
            int classifications = b.Read(6) + 1;
            b.Read(8);
            for (int c = 0; c < classifications; c++)
            {
                int low = b.Read(3);
                int high = b.Read(1) == 1 ? b.Read(5) : 0;
                int cascade = (high << 3) | low;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((cascade & (1 << bit)) != 0)
                    {
                        b.Read(8);
                    }
                }
            }

            if (b.Overrun)
            {
                return false;
            }
        }

        // §4.2.4.5 mappings
        int mappings = b.Read(6) + 1;
        for (int i = 0; i < mappings; i++)
        {
            if (b.Read(16) != 0)
            {
                return false;
            }

            int submaps = b.Read(1) == 1 ? b.Read(4) + 1 : 1;
            if (b.Read(1) == 1)
            {
                int steps = b.Read(8) + 1;
                b.Skip(2L * steps * ILog(channels - 1));
            }

            if (b.Read(2) != 0)
            {
                return false;
            }

            if (submaps > 1)
            {
                b.Skip(4L * channels);
            }

            b.Skip(24L * submaps);
            if (b.Overrun)
            {
                return false;
            }
        }

        // §4.2.4.6 modes
        int modeCount = b.Read(6) + 1;
        bool[] longBlock = new bool[modeCount];
        for (int i = 0; i < modeCount; i++)
        {
            longBlock[i] = b.Read(1) == 1;
            if (b.Read(16) != 0 || b.Read(16) != 0)
            {
                return false;
            }

            b.Read(8);
        }

        if (b.Overrun || b.Read(1) != 1)
        {
            return false;
        }

        modes = new VorbisModes(longBlock, identification.BlockSize0, identification.BlockSize1);
        return true;
    }

    private static long Lookup1Values(long entries, int dimensions)
    {
        // The greatest integer r with r^dimensions <= entries (§9.2.3).
        if (dimensions <= 0)
        {
            return -1;
        }

        long r = 1;
        while (true)
        {
            long next = r + 1;
            long power = 1;
            bool over = false;
            for (int i = 0; i < dimensions; i++)
            {
                power *= next;
                if (power > entries)
                {
                    over = true;
                    break;
                }
            }

            if (over)
            {
                return r;
            }

            r = next;
        }
    }

    internal static int ILog(int x)
    {
        int n = 0;
        while (x > 0)
        {
            n++;
            x >>= 1;
        }

        return n;
    }

    /// <summary>Gets the block size of an audio packet.</summary>
    /// <param name="packet">An audio packet.</param>
    /// <param name="blockSize">The block size in samples.</param>
    /// <returns>False for an empty packet, a header packet or a mode number out of range.</returns>
    public bool TryGetBlockSize(ReadOnlySpan<byte> packet, out int blockSize)
    {
        blockSize = 0;
        if (packet.Length < 1 || (packet[0] & 1) != 0)
        {
            return false;
        }

        int modeBits = ILog(_longBlock.Length - 1);
        int mode = (packet[0] >> 1) & ((1 << modeBits) - 1);
        if (mode >= _longBlock.Length)
        {
            return false;
        }

        blockSize = _longBlock[mode] ? LongBlockSize : ShortBlockSize;
        return true;
    }

    /// <summary>Gets the number of samples a packet adds to the output, given the previous packet's block size (Vorbis I §1.3.2.3).</summary>
    /// <param name="previousBlockSize">The previous audio packet's block size, or 0 for the first packet, which produces no output.</param>
    /// <param name="blockSize">This packet's block size.</param>
    /// <returns>The sample count: a quarter of each of the two block sizes.</returns>
    public static int GetOutputSamples(int previousBlockSize, int blockSize) => previousBlockSize == 0 ? 0 : (previousBlockSize / 4) + (blockSize / 4);

    // Reads bits least significant first, the order the Vorbis bitpacker uses (§2.1.4).
    private ref struct LsbReader
    {
        private readonly ReadOnlySpan<byte> _d;
        private long _bit;

        public LsbReader(ReadOnlySpan<byte> d)
        {
            _d = d;
            _bit = 0;
            Overrun = false;
        }

        public bool Overrun { get; private set; }

        public int Read(int count)
        {
            long v = 0;
            for (int i = 0; i < count; i++)
            {
                long byteIndex = _bit >> 3;
                if (byteIndex >= _d.Length)
                {
                    Overrun = true;
                    return 0;
                }

                v |= (long)((_d[(int)byteIndex] >> (int)(_bit & 7)) & 1) << i;
                _bit++;
            }

            return (int)v;
        }

        public void Skip(long count)
        {
            _bit += count;
            if ((_bit >> 3) > _d.Length)
            {
                Overrun = true;
            }
        }
    }
}
