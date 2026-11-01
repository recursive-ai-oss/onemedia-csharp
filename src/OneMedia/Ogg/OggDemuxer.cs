using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Codecs.Vorbis;
using RecursiveAI.OneMedia.Input;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.VorbisComment;

namespace RecursiveAI.OneMedia.Ogg;

internal sealed class OggEngine : ISequentialEngine
{
    private const int TailBytes = 72 * 1024;

    private struct Entry
    {
        public int Start;
        public int Length;
        public bool InCarry;
        public bool Key;
        public long Duration;
        public long Offset;
    }

    private struct Queued
    {
        public PacketInfo Info;
        public int Start;
        public OggLogicalBitstream? Carry;
    }

    private readonly SequentialInput _input;
    private readonly OggPageReader _pages;
    private readonly MediaLimits _limits;
    private readonly List<OggLogicalBitstream> _streams = [];
    private readonly Dictionary<uint, OggLogicalBitstream> _bySerial = [];
    private readonly List<MediaTrack> _tracks = [];
    private readonly Dictionary<uint, OggSkeletonBone> _bones = [];
    private readonly List<ReadOnlyMemory<byte>> _skeletonPackets = [];
    private readonly List<Entry> _entries = [];
    private readonly List<Queued> _queue = [];
    private readonly HashSet<uint> _reportedOrphans = [];
    private int _head;
    private bool _ended;
    private bool _sawNonBos;
    private bool _dataPhase;
    private long _dataStart = -1;
    private MediaTime _linkEnd;
    private MediaTime _duration = MediaTime.Unknown;
    private int _skeletonMajor = -1;
    private int _skeletonMinor;
    private OggPageHeader _current;
    private bool _disposed;
    private Queued _currentPacket;
    private ReadOnlyMemory<byte> _id3Bytes;

    public OggEngine(SequentialInput input, MediaOpenOptions options, DefectLog defects)
    {
        _input = input;
        _limits = options.Limits;
        Defects = defects;
        _pages = new OggPageReader(input, defects);
    }

    /// <summary>Reads the headers of every stream up to the first data and the duration from the tail.</summary>
    public async ValueTask OpenAsync(CancellationToken ct)
    {
        await SkipId3Async(ct).ConfigureAwait(false);
        await ReadHeadersAsync(ct).ConfigureAwait(false);
        await ReadTailDurationAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The ID3v2 tag in front of the first page, kept as a raw tag; <see cref="ByteRange.None"/> when there is none.</summary>
    public ByteRange Id3Prefix { get; private set; } = ByteRange.None;

    // ID3v2 §3.1: 'ID3', version, flags, 28-bit syncsafe size (+10 for a footer). The tag is skipped; its bytes stay readable as one binary tag.
    private async ValueTask SkipId3Async(CancellationToken ct)
    {
        ReadOnlyMemory<byte> head = await _input.PeekAsync(10, ct).ConfigureAwait(false);
        long size = Id3Size(head.Span);
        if (size <= 0)
        {
            return;
        }

        Id3Prefix = new ByteRange(0, size);
        if (size <= _limits.MaxTagBytes)
        {
            byte[] bytes = new byte[size];
            int got = await _input.ReadAsync(bytes, ct).ConfigureAwait(false);
            _id3Bytes = bytes.AsMemory(0, got);
            return;
        }

        await _input.SkipAsync(size, ct).ConfigureAwait(false);
    }

    internal static long Id3Size(ReadOnlySpan<byte> head) =>
        head.Length >= 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3' && head[3] != 0xFF && ((head[6] | head[7] | head[8] | head[9]) & 0x80) == 0
            ? 10 + (((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F)) + ((head[5] & 0x10) != 0 ? 10 : 0)
            : 0;


    /// <summary>The tracks of every link read so far; later links of a chained file are appended as they are reached.</summary>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <summary>The longest stream's length, from the last page's granule position; unknown for a forward-only source and for a chained file.</summary>
    public MediaTime Duration => _duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    public bool CanSeek => _input.CanSeek;

    /// <summary>Every logical stream seen so far, tracks or not, in the order of their first pages.</summary>
    public IReadOnlyList<OggLogicalBitstream> Streams => _streams;

    /// <summary>The Skeleton description of the file, or null when it has none.</summary>
    public OggSkeleton? Skeleton => _skeletonMajor < 0 ? null : new OggSkeleton(_skeletonMajor, _skeletonMinor, [.. _bones.Values], _skeletonPackets);

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        foreach (OggLogicalBitstream s in _streams)
        {
            if (s.TrackIndex == trackIndex)
            {
                s.Enabled = enabled;
            }
        }
    }

    // ---- headers -----------------------------------------------------------------------------------------------

    private async ValueTask ReadHeadersAsync(CancellationToken ct)
    {
        while (!_dataPhase)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _pages.ReadNextAsync(ct).ConfigureAwait(false))
            {
                _ended = true;
                break;
            }

            ProcessPage();
            if (_sawNonBos && AllHeadersDone())
            {
                _dataPhase = true;
            }
        }

        FinishIncomplete();
        if (_streams.Count == 0)
        {
            throw new MediaFormatException("The data holds no Ogg page.") { Kind = DefectKind.MissingRequired, Offset = 0 };
        }

        _dataPhase = true;
        _dataStart = _input.Position;
        if (_current.TotalSize > 0)
        {
            _dataStart = _current.Offset + _current.TotalSize;
            if (_queue.Count > 0)
            {
                // RFC 3533 §6 / Vorbis I §A.2: header pages hold no audio. A page that does: its packets point into a page body that the tail read
                // and later seeks replace, so drop them and read that page again as the first data page.
                Defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, _current.Offset, "A header page also holds data packets.", "ogg.data-in-header-page");
                _queue.Clear();
                _head = 0;
                _dataStart = _current.Offset;
            }
        }
    }

    private bool AllHeadersDone()
    {
        foreach (OggLogicalBitstream s in _streams)
        {
            if (s.Kind != OggStreamKind.Skeleton && !s.HeadersDone && !s.Ended)
            {
                return false;
            }
        }

        return true;
    }

    private void FinishIncomplete()
    {
        foreach (OggLogicalBitstream s in _streams)
        {
            if (s.Kind != OggStreamKind.Skeleton && !s.HeadersDone)
            {
                Defects.Add(DefectKind.Truncated, DefectSeverity.Error, s.FirstPageOffset, $"The {s.Kind} stream {s.SerialNumber} ends before all its header packets.", "ogg.headers-incomplete");
                s.HeadersDone = true;
                _tracks[s.TrackIndex] = new MediaTrack { Index = s.TrackIndex, Id = s.SerialNumber, Kind = TrackKind.Data, Timescale = 1, CodecTag = s.Kind.ToString().ToLowerInvariant() };
            }
        }
    }

    // ---- pages -------------------------------------------------------------------------------------------------

    private void ProcessPage()
    {
        _current = _pages.Header;
        OggPageHeader h = _current;
        ReadOnlySpan<byte> lacing = _pages.Lacing;
        ReadOnlySpan<byte> body = _pages.Body;
        _bySerial.TryGetValue(h.SerialNumber, out OggLogicalBitstream? s);

        if (h.IsFirst)
        {
            if (s is not null && !s.Ended)
            {
                Defects.Add(DefectKind.Duplicate, DefectSeverity.Warning, h.Offset, $"A second first page for stream {h.SerialNumber} follows pages of the same stream; it starts a new stream.", "ogg.duplicate-bos");
            }

            s = BeginStream(h, lacing, body);
        }
        else
        {
            _sawNonBos = true;
            if (s is null)
            {
                if (_reportedOrphans.Add(h.SerialNumber))
                {
                    Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, h.Offset, $"Pages of stream {h.SerialNumber} have no first page; they are ignored.", "ogg.orphan");
                }

                return;
            }
        }

        if (s.Ended && !h.IsFirst)
        {
            Defects.Add(DefectKind.InvalidOrder, DefectSeverity.Warning, h.Offset, $"A page of stream {s.SerialNumber} follows its last page; it is ignored.", "ogg.after-eos");
            return;
        }

        if (s.Seen && s.LastSequence + 1 != h.SequenceNumber)
        {
            // RFC 3533 §6: a gap in the sequence numbers means pages were lost; a packet that was being assembled cannot be completed.
            Defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, h.Offset, $"Stream {s.SerialNumber}: page sequence {h.SequenceNumber} follows {s.LastSequence}; pages are missing.", "ogg.sequence-gap");
            s.Pending = false;
            s.AssemblyLength = 0;
            s.Running = -1;
        }

        s.Seen = true;
        s.LastSequence = h.SequenceNumber;
        _entries.Clear();
        SplitPackets(s, h, lacing, body);
        if (s.HeadersDone && s.Kind != OggStreamKind.Skeleton)
        {
            try
            {
                FinishPage(s, h);
            }
            catch (ArithmeticException e)
            {
                // A granule position near the 64-bit limits cannot be turned into a time.
                throw new MediaFormatException("The granule position of a page is too large to be a time.", e) { Kind = DefectKind.Inconsistent, Offset = h.Offset };
            }
        }

        if (h.IsLast)
        {
            s.Ended = true;
            if (s.Kind == OggStreamKind.Skeleton)
            {
                s.HeadersDone = true;
            }
        }
    }

    private OggLogicalBitstream BeginStream(OggPageHeader h, ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body)
    {
        // The first packet of a first page is complete on that page for every mapping (RFC 3533 §6 recommends it; the codec specs require it).
        int firstLength = 0;
        foreach (byte l in lacing)
        {
            firstLength += l;
            if (l < 255)
            {
                break;
            }
        }

        ReadOnlySpan<byte> first = body[..Math.Min(firstLength, body.Length)];
        OggStreamKind kind = OggHeaders.Classify(first, out int expected);
        OggLogicalBitstream s = new(h.SerialNumber, kind, h.Offset) { HeadersExpected = expected };
        bool chained = _dataPhase || _sawNonBos;
        if (chained && kind != OggStreamKind.Skeleton)
        {
            bool allEnded = true;
            foreach (OggLogicalBitstream o in _streams)
            {
                allEnded &= o.Kind == OggStreamKind.Skeleton || o.Ended;
            }

            if (allEnded && _streams.Count > 0)
            {
                s.LinkBase = _linkEnd;
                Defects.Add(DefectKind.Other, DefectSeverity.Info, h.Offset, "A new link of a chained Ogg stream begins; its times continue after the earlier links.", "ogg.chained");
            }
            else
            {
                Defects.Add(DefectKind.InvalidOrder, DefectSeverity.Info, h.Offset, "A logical stream begins after data pages of other streams.", "ogg.late-bos");
            }
        }

        if (kind != OggStreamKind.Skeleton)
        {
            _limits.CheckTracks(_tracks.Count + 1, h.Offset);
            s.TrackIndex = _tracks.Count;
            _tracks.Add(new MediaTrack { Index = s.TrackIndex, Id = h.SerialNumber, Kind = TrackKind.Unknown, Timescale = 1 });
        }

        if (kind == OggStreamKind.Unknown)
        {
            s.HeadersDone = true;
            _tracks[s.TrackIndex] = new MediaTrack
            {
                Index = s.TrackIndex,
                Id = h.SerialNumber,
                Kind = TrackKind.Data,
                Timescale = 1,
                CodecTag = OggHeaders.PrintableTag(first),
                Name = SkeletonName(h.SerialNumber),
                Language = SkeletonLanguage(h.SerialNumber),
            };
        }

        if (kind == OggStreamKind.Skeleton && first.Length >= 12)
        {
            _skeletonMajor = BinaryPrimitives.ReadUInt16LittleEndian(first[8..]);
            _skeletonMinor = BinaryPrimitives.ReadUInt16LittleEndian(first[10..]);
        }

        _streams.Add(s);
        _bySerial[h.SerialNumber] = s;
        return s;
    }

    private string? SkeletonName(uint serial) => _bones.TryGetValue(serial, out OggSkeletonBone? b) ? b.Header("Name") : null;

    private string? SkeletonLanguage(uint serial) => _bones.TryGetValue(serial, out OggSkeletonBone? b) ? b.Header("Language") : null;

    // RFC 3533 §5: a packet's size is the sum of its lacing values, ending at the first value below 255; a value of 255 on the
    // last segment means the packet goes on in the next page (whose "continued" flag is then set).
    private void SplitPackets(OggLogicalBitstream s, OggPageHeader h, ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body)
    {
        bool continued = (h.Flags & OggPageType.Continued) != 0;
        if (!continued && s.Pending)
        {
            Defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, h.Offset, $"Stream {s.SerialNumber}: a packet is cut off where the next page does not continue it.", "ogg.continuation-missing");
            s.Pending = false;
            s.AssemblyLength = 0;
        }

        bool dropFirst = continued && !s.Pending;
        if (dropFirst && s.Seen && s.Running >= 0)
        {
            Defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, h.Offset, $"Stream {s.SerialNumber}: the page continues a packet whose beginning was not seen; the fragment is dropped.", "ogg.continuation-orphan");
        }

        int segEnd = 0;
        int fragStart = 0;
        bool first = true;
        for (int i = 0; i < lacing.Length; i++)
        {
            segEnd += lacing[i];
            if (lacing[i] == 255)
            {
                if (i == lacing.Length - 1)
                {
                    // The packet goes on in the next page: keep what is here.
                    if (!dropFirst || !first)
                    {
                        if (!s.Pending)
                        {
                            s.PacketStartPage = h.Offset;
                        }

                        Append(s, body[fragStart..segEnd], h.Offset);
                        s.Pending = true;
                    }

                    fragStart = segEnd;
                }

                continue;
            }

            ReadOnlySpan<byte> frag = body[fragStart..segEnd];
            if (first && continued)
            {
                if (s.Pending)
                {
                    Append(s, frag, h.Offset);
                    (s.Assembly, s.Carry) = (s.Carry, s.Assembly);
                    int len = s.AssemblyLength;
                    s.AssemblyLength = 0;
                    s.Pending = false;
                    OnPacket(s, s.Carry.AsSpan(0, len), 0, len, true, h);
                }
            }
            else
            {
                s.PacketStartPage = h.Offset;
                OnPacket(s, frag, fragStart, frag.Length, false, h);
            }

            first = false;
            fragStart = segEnd;
        }
    }

    private void Append(OggLogicalBitstream s, ReadOnlySpan<byte> fragment, long offset)
    {
        long need = (long)s.AssemblyLength + fragment.Length;
        _limits.CheckPacket(need, offset);
        if (need > s.Assembly.Length)
        {
            byte[] bigger = new byte[Math.Max(need, Math.Min(s.Assembly.Length * 2L, int.MaxValue))];
            s.Assembly.AsSpan(0, s.AssemblyLength).CopyTo(bigger);
            s.Assembly = bigger;
        }

        fragment.CopyTo(s.Assembly.AsSpan(s.AssemblyLength));
        s.AssemblyLength += fragment.Length;
    }

    // ---- packets -----------------------------------------------------------------------------------------------

    private void OnPacket(OggLogicalBitstream s, ReadOnlySpan<byte> data, int start, int length, bool inCarry, OggPageHeader h)
    {
        if (!s.HeadersDone || s.Kind == OggStreamKind.Skeleton)
        {
            OnHeaderPacket(s, data, h);
            return;
        }

        bool audio = s.Kind != OggStreamKind.Theora && s.Kind != OggStreamKind.Unknown;
        if (length == 0 && audio)
        {
            return;
        }

        bool key = s.Kind switch
        {
            OggStreamKind.Theora => length > 0 && (data[0] & 0xC0) == 0,   // Theora §7.1: bit 7 = 0 for data packets, bit 6 = 0 for intra frames
            OggStreamKind.Unknown => false,
            _ => true,
        };
        long offset = inCarry ? -1 : h.Offset + h.HeaderSize + start;
        _entries.Add(new Entry { Start = start, Length = length, InCarry = inCarry, Key = key, Offset = offset });
    }

    private void OnHeaderPacket(OggLogicalBitstream s, ReadOnlySpan<byte> data, OggPageHeader h)
    {
        if (s.Kind == OggStreamKind.Skeleton)
        {
            _limits.CheckAllocation(data.Length, h.Offset);
            _skeletonPackets.Add(data.ToArray());
            if (data.Length >= 8 && data[..8].SequenceEqual("fisbone\0"u8) && OggHeaders.ParseFisbone(data) is { } bone)
            {
                _bones[bone.SerialNumber] = bone;
            }

            return;
        }

        if (s.Kind == OggStreamKind.Flac && s.HeadersExpected == int.MaxValue && s.Headers.Count > 0 && data.Length > 0 && data[0] == 0xFF)
        {
            // The mapping left the header count unknown, so the first audio frame (sync code 0xFF) ends the headers. It is handled below as data.
            FinishHeaders(s);
            OnPacket(s, data, 0, data.Length, true, h);
            return;
        }

        _limits.CheckAllocation(data.Length, h.Offset);
        s.Headers.Add(data.ToArray());
        s.HeaderPageRanges.Add((s.PacketStartPage, h.Offset + h.TotalSize));
        if (s.Headers.Count >= s.HeadersExpected)
        {
            FinishHeaders(s);
        }
    }

    private void FinishHeaders(OggLogicalBitstream s)
    {
        s.HeadersDone = true;
        MediaTrack? track = null;
        try
        {
            track = s.Kind switch
            {
                OggStreamKind.Vorbis => BuildVorbis(s),
                OggStreamKind.Opus => BuildOpus(s),
                OggStreamKind.Flac => BuildFlac(s),
                OggStreamKind.Theora => BuildTheora(s),
                OggStreamKind.Speex => BuildSpeex(s),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            track = null;
        }

        if (track is null)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, s.FirstPageOffset, $"The {s.Kind} headers of stream {s.SerialNumber} are not valid; the stream is kept as unknown data.", "ogg.bad-headers");
            track = new MediaTrack { Index = s.TrackIndex, Id = s.SerialNumber, Kind = TrackKind.Data, Timescale = 1, CodecTag = s.Kind.ToString().ToLowerInvariant() };
        }

        _tracks[s.TrackIndex] = track;
    }

    private MediaTrack? BuildVorbis(OggLogicalBitstream s)
    {
        if (s.Headers.Count < 3 || !VorbisIdentificationHeader.TryParse(s.Headers[0], out VorbisIdentificationHeader id)
            || VorbisIdentificationHeader.GetHeaderType(s.Headers[1]) != VorbisIdentificationHeader.PacketTypeComment
            || VorbisIdentificationHeader.GetHeaderType(s.Headers[2]) != VorbisIdentificationHeader.PacketTypeSetup)
        {
            return null;
        }

        s.Vorbis = id;
        if (VorbisModes.TryParse(s.Headers[2], in id, out VorbisModes? modes))
        {
            s.Modes = modes;
        }
        else
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, s.FirstPageOffset, "The Vorbis setup header cannot be read, so packet durations are unknown.", "ogg.vorbis-setup");
        }

        s.Timescale = id.SampleRate;
        ArrayBufferWriter<byte> w = new();
        VorbisXiphLacing.Build(s.Headers[0], s.Headers[1], s.Headers[2], w);
        return new MediaTrack
        {
            Index = s.TrackIndex,
            Id = s.SerialNumber,
            Kind = TrackKind.Audio,
            Codec = CodecId.Vorbis,
            Configuration = new CodecConfiguration(w.WrittenMemory.ToArray(), "CodecPrivate"),
            Timescale = id.SampleRate,
            Audio = new AudioTrackInfo(id.SampleRate, id.Channels),
            BitRate = Math.Max(id.BitrateNominal, 0),
            Name = SkeletonName(s.SerialNumber),
            Language = SkeletonLanguage(s.SerialNumber),
        };
    }

    private MediaTrack? BuildOpus(OggLogicalBitstream s)
    {
        if (s.Headers.Count < 2 || !OpusHead.TryParseOpusHead(s.Headers[0], out OpusHead? head) || head is null || !s.Headers[1].AsSpan().StartsWith("OpusTags"u8))
        {
            return null;
        }

        // RFC 7845 §4: Opus always decodes at 48 kHz; the granule position counts 48 kHz samples including the pre-skip.
        s.Timescale = 48000;
        s.PreSkip = head.PreSkip;
        s.PtsOrigin = head.PreSkip;
        return new MediaTrack
        {
            Index = s.TrackIndex,
            Id = s.SerialNumber,
            Kind = TrackKind.Audio,
            Codec = CodecId.Opus,
            Configuration = new CodecConfiguration(s.Headers[0], "OpusHead"),
            Timescale = 48000,
            Audio = new AudioTrackInfo(48000, head.ChannelCount),
            Edit = new TrackEditInfo(Priming: new MediaTime(head.PreSkip, 48000)),
            Name = SkeletonName(s.SerialNumber),
            Language = SkeletonLanguage(s.SerialNumber),
        };
    }

    private MediaTrack? BuildFlac(OggLogicalBitstream s)
    {
        // Xiph's FLAC-in-Ogg mapping: 0x7F "FLAC", version 1.0, header count, "fLaC", then the STREAMINFO block with its header.
        byte[] first = s.Headers[0];
        if (first.Length < 13 + FlacBlockHeader.Size + FlacStreamInfo.BodyLength || !first.AsSpan(9, 4).SequenceEqual("fLaC"u8)
            || !FlacConfig.TryGetStreamInfo(first.AsSpan(13), out FlacStreamInfo info))
        {
            return null;
        }

        s.FlacMinBlock = Math.Max(info.MinBlockSize, 1);
        s.Timescale = info.SampleRate;
        byte[] config = new byte[4 + FlacBlockHeader.Size + FlacStreamInfo.BodyLength];
        "fLaC"u8.CopyTo(config);
        first.AsSpan(13, FlacBlockHeader.Size + FlacStreamInfo.BodyLength).CopyTo(config.AsSpan(4));
        config[4] |= 0x80;
        return new MediaTrack
        {
            Index = s.TrackIndex,
            Id = s.SerialNumber,
            Kind = TrackKind.Audio,
            Codec = CodecId.Flac,
            Configuration = new CodecConfiguration(config, "CodecPrivate"),
            Timescale = info.SampleRate,
            Duration = info.TotalSamples > 0 ? new MediaTime(info.TotalSamples, info.SampleRate) : MediaTime.Unknown,
            Audio = new AudioTrackInfo(info.SampleRate, info.Channels, info.BitsPerSample),
            Name = SkeletonName(s.SerialNumber),
            Language = SkeletonLanguage(s.SerialNumber),
        };
    }

    private MediaTrack? BuildTheora(OggLogicalBitstream s)
    {
        if (s.Headers.Count < 3 || !OggHeaders.TryParseTheora(s.Headers[0], out int major, out int minor, out int rev, out int w, out int h, out int frn, out int frd, out int parn, out int pard, out int shift)
            || s.Headers[1].Length < 7 || s.Headers[1][0] != 0x81 || s.Headers[2].Length < 7 || s.Headers[2][0] != 0x82)
        {
            return null;
        }

        s.TheoraShift = shift;
        // Theora §A.2: streams of version 3.2.1 and later count granule positions from the frame itself; older ones from 1.
        s.TheoraNewGranule = (major, minor, rev).CompareTo((3, 2, 1)) >= 0;
        s.Timescale = frn;
        s.UnitTicks = frd;
        ArrayBufferWriter<byte> cfg = new();
        VorbisXiphLacing.Build(s.Headers[0], s.Headers[1], s.Headers[2], cfg);
        return new MediaTrack
        {
            Index = s.TrackIndex,
            Id = s.SerialNumber,
            Kind = TrackKind.Video,
            Codec = new CodecId("theora"),
            Configuration = new CodecConfiguration(cfg.WrittenMemory.ToArray(), "CodecPrivate"),
            Timescale = frn,
            Video = new VideoTrackInfo(w, h, 0, parn > 0 ? parn : 1, pard > 0 ? pard : 1, frn, frd),
            Name = SkeletonName(s.SerialNumber),
            Language = SkeletonLanguage(s.SerialNumber),
        };
    }

    private MediaTrack? BuildSpeex(OggLogicalBitstream s)
    {
        // Speex header (80 bytes): rate at 36, mode at 40, channels at 48, bitrate at 52, frame_size at 56, frames_per_packet at 64.
        byte[] p = s.Headers[0];
        if (p.Length < 72)
        {
            return null;
        }

        int rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(36)), int.MaxValue);
        int channels = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(48)), 255);
        int bitrate = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(52));
        int frameSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(56)), 1 << 20);
        int perPacket = (int)Math.Min(Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(64)), 1), 1 << 10);
        if (rate <= 0 || channels <= 0 || frameSize <= 0)
        {
            return null;
        }

        s.Timescale = rate;
        s.SpeexFrameSamples = frameSize * perPacket;
        return new MediaTrack
        {
            Index = s.TrackIndex,
            Id = s.SerialNumber,
            Kind = TrackKind.Audio,
            Codec = new CodecId("speex"),
            Configuration = new CodecConfiguration(p, "CodecPrivate"),
            Timescale = rate,
            Audio = new AudioTrackInfo(rate, channels),
            BitRate = Math.Max(bitrate, 0),
            Name = SkeletonName(s.SerialNumber),
            Language = SkeletonLanguage(s.SerialNumber),
        };
    }

    // ---- timing ------------------------------------------------------------------------------------------------

    private static long DurationOf(OggLogicalBitstream s, ReadOnlySpan<byte> data)
    {
        switch (s.Kind)
        {
            case OggStreamKind.Vorbis:
                if (s.Modes is { } modes && modes.TryGetBlockSize(data, out int bs))
                {
                    // Vorbis I §1.3.2.3: a packet yields a quarter of the previous and of its own block size. The first packet yields nothing by the
                    // specification; the common tools count it as half its block, so the stream starts that much before 0 (observed in ffprobe).
                    int prev = s.PrevBlock == 0 ? bs : s.PrevBlock;
                    s.PrevBlock = bs;
                    return VorbisModes.GetOutputSamples(prev, bs);
                }

                return 0;
            case OggStreamKind.Opus:
                return OpusToc.TryGetSamples(data, out int n) ? n : 0;
            case OggStreamKind.Flac:
                return FlacFrameHeader.TryParse(data, out FlacFrameHeader fh) ? fh.BlockSize : s.FlacMinBlock;
            case OggStreamKind.Speex:
                return s.SpeexFrameSamples;
            case OggStreamKind.Theora:
                return 1;
            default:
                return 0;
        }
    }

    private static long EndOf(OggLogicalBitstream s, long granule)
    {
        if (s.Kind != OggStreamKind.Theora)
        {
            return granule;
        }

        // Theora §A.2.3: the granule is (keyframe number << shift) | frames since the keyframe. From version 3.2.1 the numbers count frames
        // from 1, so the sum is already the number of frames up to the packet; before that they count from 0 and one is added.
        // Observed in files written by libtheora 1.1 (first data page 0x40 for the first frame).
        long mask = (1L << s.TheoraShift) - 1;
        return (granule >> s.TheoraShift) + (granule & mask) + (s.TheoraNewGranule ? 0 : 1);
    }

    private void FinishPage(OggLogicalBitstream s, OggPageHeader h)
    {
        int n = _entries.Count;
        if (n == 0)
        {
            return;
        }

        ReadOnlySpan<byte> body = _pages.Body;
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            Entry e = _entries[i];
            ReadOnlySpan<byte> data = e.InCarry ? s.Carry.AsSpan(0, e.Length) : body.Slice(e.Start, e.Length);
            e.Duration = DurationOf(s, data);
            sum += e.Duration;
            _entries[i] = e;
        }

        bool timeline = s.Kind != OggStreamKind.Unknown;
        long start = 0;
        long trim = 0;
        if (timeline)
        {
            bool valid = h.GranulePosition >= 0;
            long end = valid ? EndOf(s, h.GranulePosition) : -1;
            long forwardEnd = s.Running >= 0 ? s.Running + sum : -1;
            if (valid && s.Running < 0)
            {
                start = end - sum;
            }
            else if (valid && end == forwardEnd)
            {
                start = s.Running;
            }
            else if (valid && h.IsLast && end < forwardEnd && end >= s.Running)
            {
                // RFC 7845 §4.5 and Vorbis I §A.2: the last page's granule position may end the stream before the last packet's samples do.
                start = s.Running;
                trim = forwardEnd - end;
            }
            else if (valid)
            {
                Defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, h.Offset, $"Stream {s.SerialNumber}: the granule position {h.GranulePosition} disagrees with the packet durations ({forwardEnd}).", "ogg.granule-mismatch");
                start = end - sum;
            }
            else
            {
                start = s.Running >= 0 ? s.Running : 0;
            }

            s.Running = valid ? end : start + sum;
            if (valid)
            {
                s.LastGranule = h.GranulePosition;
            }

            MediaTime pageEnd = new((s.Running - s.PtsOrigin) * s.UnitTicks, s.Timescale);
            if (s.LinkBase.IsKnown)
            {
                pageEnd += s.LinkBase.Rescale(s.Timescale, MediaRounding.NearestAwayFromZero); // the earlier link may count in another timescale
            }

            if (pageEnd > _linkEnd || !_linkEnd.IsKnown)
            {
                _linkEnd = pageEnd;
            }
        }

        if (!s.Enabled)
        {
            return;
        }

        long pos = start;
        TrackIndexCheck(s);
        for (int i = 0; i < n; i++)
        {
            Entry e = _entries[i];
            if (s.WaitForKeyframe)
            {
                if (!e.Key)
                {
                    pos += e.Duration;
                    continue;
                }

                s.WaitForKeyframe = false;
            }

            long duration = e.Duration;
            MediaTime discard = default;
            if (trim > 0 && i == n - 1)
            {
                long cut = Math.Min(trim, duration);
                duration -= cut;
                discard = new MediaTime(cut, s.Timescale);
            }

            MediaTime pts;
            MediaTime dur;
            if (timeline)
            {
                pts = new MediaTime((pos - s.PtsOrigin) * s.UnitTicks, s.Timescale);
                if (s.LinkBase.IsKnown)
                {
                    pts += s.LinkBase.Rescale(s.Timescale, MediaRounding.NearestAwayFromZero);
                }

                dur = new MediaTime(duration * s.UnitTicks, s.Timescale);
            }
            else
            {
                pts = MediaTime.Unknown;
                dur = MediaTime.Unknown;
            }

            PacketAttributes flags = e.Key ? PacketAttributes.Keyframe : PacketAttributes.None;
            ByteRange range = e.Offset >= 0 ? new ByteRange(e.Offset, e.Length) : ByteRange.None;
            _queue.Add(new Queued
            {
                Info = new PacketInfo(s.TrackIndex, pts, pts, dur, flags, range, e.Length, discard),
                Start = e.Start,
                Carry = e.InCarry ? s : null,
            });
            pos += e.Duration;
        }
    }

    private static void TrackIndexCheck(OggLogicalBitstream s)
    {
        // A stream without a track index never reaches here (Skeleton is handled as headers).
        if (s.TrackIndex < 0)
        {
            throw new InvalidOperationException("A data page of a stream without a track.");
        }
    }

    // ---- reading -----------------------------------------------------------------------------------------------

    /// <summary>Advances to the next packet; false at the end. The packet is then in <see cref="Current"/> until the next call.</summary>
    public async ValueTask<bool> NextAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            if (_head < _queue.Count)
            {
                _currentPacket = _queue[_head++];
                return true;
            }

            _queue.Clear();
            _head = 0;
            if (_ended || !await _pages.ReadNextAsync(ct).ConfigureAwait(false))
            {
                _ended = true;
                FinishIncomplete();
                return false;
            }

            ProcessPage();
        }
    }

    /// <summary>The packet found by the last <see cref="NextAsync"/>.</summary>
    public PacketView Current
    {
        get
        {
            Queued q = _currentPacket;
            ReadOnlySpan<byte> data = q.Carry is { } c ? c.Carry.AsSpan(0, q.Info.Size) : _pages.Body.Slice(q.Start, q.Info.Size);
            return new PacketView(q.Info, data);
        }
    }

    // ---- duration ----------------------------------------------------------------------------------------------

    private async ValueTask ReadTailDurationAsync(CancellationToken ct)
    {
        if (!_input.CanSeek || _input.Length <= 0)
        {
            return;
        }

        long length = _input.Length;
        int size = (int)Math.Min(length, TailBytes);
        byte[] tail = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int got = await _input.ReadAtAsync(length - size, tail.AsMemory(0, size), ct).ConfigureAwait(false);
            Dictionary<uint, long> last = [];
            bool foreign = false;
            ReadOnlySpan<byte> span = tail.AsSpan(0, got);
            int from = 0;
            while (from < span.Length - OggPageReader.FixedHeaderSize)
            {
                int at = span[from..].IndexOf("OggS"u8);
                if (at < 0)
                {
                    break;
                }

                at += from;
                if (OggPageReader.TryParseHeader(span[at..], length - size + at, out OggPageHeader h) && at + h.TotalSize <= span.Length
                    && OggPageReader.Crc(span.Slice(at, h.TotalSize)) == h.Checksum)
                {
                    if (!_bySerial.ContainsKey(h.SerialNumber))
                    {
                        foreign = true;
                    }
                    else if (h.GranulePosition >= 0)
                    {
                        last[h.SerialNumber] = h.GranulePosition;
                    }

                    from = at + h.TotalSize;
                }
                else
                {
                    from = at + 1;
                }
            }

            if (foreign)
            {
                // The last pages belong to another link, so the tail says nothing about the first link's length.
                return;
            }

            MediaTime best = MediaTime.Unknown;
            foreach (OggLogicalBitstream s in _streams)
            {
                if (s.Kind is OggStreamKind.Skeleton or OggStreamKind.Unknown || !last.TryGetValue(s.SerialNumber, out long g))
                {
                    continue;
                }

                s.LastGranule = g;
                long units = EndOf(s, g) - s.PtsOrigin;
                MediaTime d = new(units * s.UnitTicks, s.Timescale);
                MediaTrack t = _tracks[s.TrackIndex];
                if (t.Duration.IsKnown && t.Kind == TrackKind.Audio && t.Codec == CodecId.Flac)
                {
                    d = t.Duration;
                }

                _tracks[s.TrackIndex] = t with { Duration = d };
                if (!best.IsKnown || d > best)
                {
                    best = d;
                }
            }

            _duration = best;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tail);
            _input.Seek(_dataStart >= 0 ? _dataStart : 0);
        }
    }

    // ---- seeking -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Seeks to a page boundary near <paramref name="target"/> by bisection on page granule positions. The first audio track is the reference
    /// (the first enabled track otherwise); for a Theora reference the seek lands before the keyframe that governs the target and the
    /// packets before that keyframe are dropped. Times after a seek come from the next granule position.
    /// </summary>
    /// <param name="target">The time to seek to.</param>
    /// <param name="mode"><see cref="SeekMode.PreviousKeyframe"/> lands at or before the target, <see cref="SeekMode.NextKeyframe"/> at the first page boundary after it.</param>
    /// <param name="ct">Observed while pages are read.</param>
    /// <returns>The time of the first packet that will be read, or <see cref="MediaTime.Zero"/> at the start.</returns>
    /// <exception cref="NotSupportedException">The source cannot seek.</exception>
    public async ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_input.CanSeek)
        {
            throw new NotSupportedException("The Ogg source cannot seek.");
        }

        OggLogicalBitstream? reference = null;
        foreach (OggLogicalBitstream s in _streams)
        {
            if (s.TrackIndex < 0 || !s.Enabled || s.Kind == OggStreamKind.Unknown)
            {
                continue;
            }

            if (reference is null || (_tracks[s.TrackIndex].Kind == TrackKind.Audio && _tracks[reference.TrackIndex].Kind != TrackKind.Audio))
            {
                reference = s;
            }
        }

        _queue.Clear();
        _head = 0;
        _ended = false;
        foreach (OggLogicalBitstream s in _streams)
        {
            s.Pending = false;
            s.AssemblyLength = 0;
            s.Running = -1;
            s.PrevBlock = 0;
            s.Seen = false;
            s.Ended = false;
        }

        if (reference is null || target <= MediaTime.Zero)
        {
            _input.Seek(_dataStart);
            ResetKeyWait(reference);
            return MediaTime.Zero;
        }

        long targetUnits = target.Rescale(reference.Timescale, MediaRounding.Floor).Ticks / Math.Max(reference.UnitTicks, 1) + reference.PtsOrigin;
        PageAt found = await FindLastPageAsync(reference, g => EndOf(reference, g) <= targetUnits, ct).ConfigureAwait(false);
        if (reference.Kind == OggStreamKind.Theora && found.Offset >= 0)
        {
            long keyframe = (found.Granule >> reference.TheoraShift) - (reference.TheoraNewGranule ? 1 : 0);
            PageAt before = await FindLastPageAsync(reference, g => EndOf(reference, g) <= keyframe, ct).ConfigureAwait(false);
            found = before;
            reference.WaitForKeyframe = true;
        }

        if (mode == SeekMode.NextKeyframe && found.Offset >= 0)
        {
            PageAt next = await NextPageOfStreamAsync(reference, found.End, new byte[OggPageReader.FixedHeaderSize + 255], long.MaxValue, ct).ConfigureAwait(false);
            if (next.Offset >= 0)
            {
                found = next;
            }
        }

        if (found.Offset < 0)
        {
            _input.Seek(_dataStart);
            ResetKeyWait(reference);
            return MediaTime.Zero;
        }

        _input.Seek(found.End);
        return new MediaTime((EndOf(reference, found.Granule) - reference.PtsOrigin) * reference.UnitTicks, reference.Timescale);
    }

    private static void ResetKeyWait(OggLogicalBitstream? reference)
    {
        if (reference is { Kind: OggStreamKind.Theora })
        {
            reference.WaitForKeyframe = true;
        }
    }

    private readonly record struct PageAt(long Offset, long End, long Granule);

    // The last page of the stream, in the data part of the file, whose granule position satisfies the predicate; found by bisection and
    // then a short forward scan. Granule positions grow with file position within one stream.
    private async ValueTask<PageAt> FindLastPageAsync(OggLogicalBitstream stream, Func<long, bool> notAfter, CancellationToken ct)
    {
        long lo = _dataStart;
        long hi = _input.Length;
        PageAt best = new(-1, -1, -1);
        byte[] buf = new byte[OggPageReader.FixedHeaderSize + 255];
        while (hi - lo > 128 * 1024)
        {
            long mid = lo + ((hi - lo) / 2);
            PageAt p = await NextPageOfStreamAsync(stream, mid, buf, hi, ct).ConfigureAwait(false);
            if (p.Offset < 0 || !notAfter(p.Granule))
            {
                hi = p.Offset >= 0 ? Math.Min(hi, mid) : mid;
            }
            else
            {
                best = p;
                lo = p.End;
            }
        }

        long at = lo;
        while (at < hi)
        {
            PageAt p = await NextPageOfStreamAsync(stream, at, buf, long.MaxValue, ct).ConfigureAwait(false);
            if (p.Offset < 0 || !notAfter(p.Granule))
            {
                break;
            }

            best = p;
            at = p.End;
        }

        return best;
    }

    private async ValueTask<PageAt> NextPageOfStreamAsync(OggLogicalBitstream stream, long from, byte[] buf, long limit, CancellationToken ct)
    {
        long length = _input.Length;
        long at = from;
        byte[] scan = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (at < length)
            {
                int got = await _input.ReadAtAsync(at, scan.AsMemory(0, (int)Math.Min(scan.Length, length - at)), ct).ConfigureAwait(false);
                if (got < OggPageReader.FixedHeaderSize)
                {
                    return new PageAt(-1, -1, -1);
                }

                int i = scan.AsSpan(0, got).IndexOf("OggS"u8);
                if (i < 0)
                {
                    at += Math.Max(1, got - 3);
                    continue;
                }

                long pageAt = at + i;
                int hb = await _input.ReadAtAsync(pageAt, buf.AsMemory(0, (int)Math.Min(buf.Length, length - pageAt)), ct).ConfigureAwait(false);
                if (OggPageReader.TryParseHeader(buf.AsSpan(0, hb), pageAt, out OggPageHeader h) && pageAt + h.TotalSize <= length)
                {
                    if (h.SerialNumber == stream.SerialNumber && h.GranulePosition >= 0 && pageAt <= limit)
                    {
                        return new PageAt(pageAt, pageAt + h.TotalSize, h.GranulePosition);
                    }

                    at = pageAt + h.TotalSize;
                }
                else
                {
                    at = pageAt + 1;
                }

                if (at > limit)
                {
                    return new PageAt(-1, -1, -1);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scan);
        }

        return new PageAt(-1, -1, -1);
    }

    // ---- metadata ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the Vorbis comments of every stream. Each comment header is already in memory from opening, so this reads nothing more. Offsets in
    /// the tags are relative to the start of the comment packet (a packet can span pages); the container range is the pages that hold it.
    /// The first stream with comments describes the file (track id 0); the others carry their track id.
    /// </summary>
    /// <returns>The tags, with duration and, where the comments say, capture time and software as facts.</returns>
    public MediaMetadata ReadMetadata()
    {
        List<ITagContainer> containers = [];
        bool fileLevelTaken = false;
        DefectLog defects = new(_limits);
        if (!_id3Bytes.IsEmpty)
        {
            containers.Add(RawId3Prefix.Make(_id3Bytes, _limits, defects));
        }

        foreach (OggLogicalBitstream s in _streams)
        {
            if (s.TrackIndex < 0 || CommentBody(s, out ReadOnlyMemory<byte> body) is not { } range)
            {
                continue;
            }

            long trackId = fileLevelTaken ? s.SerialNumber : 0;
            fileLevelTaken = true;
            containers.Add(VorbisCommentReader.Read(body, trackId, _limits, defects, range));
        }

        FactsBuilder facts = new(defects);
        foreach (ITagContainer c in containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        if (_duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(_duration, "container", _duration.ToString(), "granule position of the last page"));
        }

        foreach (MediaTrack t in _tracks)
        {
            if (t.Video is { } v)
            {
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "theora:identification", $"{v.Width}x{v.Height}", "picture size of the Theora identification header"));
                break;
            }
        }

        return new MediaMetadata(containers, [], facts.Build(), defects, true);
    }

    // The body of the comment header, past the codec's magic, and the pages that hold it; null when the stream has none.
    private static ByteRange? CommentBody(OggLogicalBitstream s, out ReadOnlyMemory<byte> body)
    {
        body = default;
        int index = s.Kind switch { OggStreamKind.Vorbis or OggStreamKind.Opus or OggStreamKind.Theora or OggStreamKind.Speex => 1, OggStreamKind.Flac => 1, _ => -1 };
        if (index < 0 || s.Headers.Count <= index)
        {
            return null;
        }

        byte[] packet = s.Headers[index];
        (long from, long to) = s.HeaderPageRanges[index];
        ByteRange range = new(from, to - from);
        switch (s.Kind)
        {
            case OggStreamKind.Vorbis when packet.Length >= 7:
                body = packet.AsMemory(7);
                return range;
            case OggStreamKind.Theora when packet.Length >= 7:
                body = packet.AsMemory(7);
                return range;
            case OggStreamKind.Opus when packet.Length >= 8:
                body = packet.AsMemory(8);
                return range;
            case OggStreamKind.Speex:
                body = packet;
                return range;
            case OggStreamKind.Flac when packet.Length >= 4 && (packet[0] & 0x7F) == 4:
                body = packet.AsMemory(4);
                return range;
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _input.Dispose();
    }
}


/// <summary>
/// Reads Ogg (RFC 3533): pages with their checksums, packets that span pages, and every logical stream of the file, multiplexed or chained.
/// Vorbis, Opus (RFC 7845), FLAC, Theora and Speex streams become tracks with their codec configuration; Skeleton is read as metadata
/// (<see cref="Skeleton"/>); any other stream is kept as an unknown data track. The reader works on forward-only sources.
/// </summary>
/// <remarks>
/// Times come from granule positions. A packet's start is found by counting back from the granule of the page it ends on, so the
/// first packets of a stream may start before 0 (Vorbis counts its first packet as half a block, as the common tools do). On the last page of a
/// stream a granule below the packets' total trims the last packet: its duration shrinks and <see cref="PacketInfo.DiscardPadding"/> says by how much.
/// Opus times are on the 48 kHz clock with the pre-skip subtracted (RFC 7845 §4.4).
/// The duration of a seekable file is read from the last pages in a tail read of at most 72 KiB; nothing else is scanned.
/// A chained file is read link by link: the tracks of later links are appended to <see cref="Tracks"/> when the reader reaches them and
/// their times continue after the end of the earlier links.
/// </remarks>
public sealed class OggDemuxer : IDemuxer, IMetadataProvider
{
    private readonly OggEngine _engine;
    private readonly IDisposable? _owned;

    private OggDemuxer(OggEngine engine, IDisposable? owned)
    {
        _engine = engine;
        _owned = owned;
    }

    /// <summary>Opens a seekable source.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership; <see cref="MediaOpenOptions.Default"/> if null.</param>
    /// <param name="cancellationToken">Observed while the headers are read.</param>
    /// <returns>The demuxer, positioned at the first packet.</returns>
    /// <exception cref="MediaFormatException">The source does not hold this format.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded before the first packet.</exception>
    public static OggDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Create(new SequentialInput(source), source, options ?? MediaOpenOptions.Default, cancellationToken);
    }

    /// <summary>Opens a source that can only be read once, front to back. Nothing that needs a seek is available: the duration may be unknown and <see cref="CanSeek"/> is false.</summary>
    /// <param name="source">The source, at the start of the data.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the headers are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The source does not hold this format.</exception>
    public static OggDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Create(new SequentialInput(source), source, options ?? MediaOpenOptions.Default, cancellationToken);
    }

    internal static OggDemuxer Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        SequentialInput input = new(source);
        input.Seed(head);
        return Create(input, source, options, cancellationToken);
    }

    private static OggDemuxer Create(SequentialInput input, IDisposable source, MediaOpenOptions options, CancellationToken ct)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        IDisposable? owned = options.LeaveOpen ? null : source;
        OggEngine engine = new OggEngine(input, options, defects);
        return new OggDemuxer(EngineHost.Open(engine, owned, ct), owned);
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Ogg;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _engine.Tracks;

    /// <summary>The length of the media, or unknown when the format and source do not state it.</summary>
    public MediaTime Duration => _engine.Duration;

    /// <inheritdoc />
    public DefectLog Defects => _engine.Defects;

    /// <inheritdoc />
    public bool CanSeek => _engine.CanSeek;

    /// <summary>Every logical bitstream seen so far, tracks or not, in the order of their first pages.</summary>
    public IReadOnlyList<OggLogicalBitstream> Streams => _engine.Streams;

    /// <summary>The Skeleton description of the file, or null when it has none.</summary>
    public OggSkeleton? Skeleton => _engine.Skeleton;

    /// <summary>The ID3v2 tag in front of the first page, or <see cref="ByteRange.None"/>; the tag is skipped and not parsed.</summary>
    public ByteRange Id3Prefix => _engine.Id3Prefix;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _engine.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        if (!EngineHost.Wait(_engine.NextAsync(CancellationToken.None)))
        {
            packet = default;
            return false;
        }

        packet = _engine.Current;
        return true;
    }

    /// <inheritdoc />
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe) => EngineHost.Wait(_engine.SeekAsync(target, mode, CancellationToken.None));

    /// <inheritdoc />
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default) => _engine.ReadMetadata();

    /// <inheritdoc />
    public void Dispose()
    {
        _engine.Dispose();
        _owned?.Dispose();
    }
}

/// <summary>The asynchronous twin of <see cref="OggDemuxer"/> for ranged and remote sources: every read of the source is awaited.</summary>
public sealed class OggAsyncDemuxer : IAsyncDemuxer, IAsyncMetadataProvider
{
    private readonly OggEngine _engine;
    private readonly IAsyncDisposable? _owned;

    private OggAsyncDemuxer(OggEngine engine, IAsyncDisposable? owned)
    {
        _engine = engine;
        _owned = owned;
    }

    /// <summary>Opens an asynchronous source. Tag values and the other metadata of the leading part of the file are kept in memory, within the limits, so they can be read without further I/O.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the headers are read.</param>
    /// <returns>The demuxer, positioned at the first packet.</returns>
    /// <exception cref="MediaFormatException">The source does not hold this format.</exception>
    public static async ValueTask<OggAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        IAsyncDisposable? owned = options.LeaveOpen ? null : source;
        SequentialInput input = new(source);
        OggEngine engine = new OggEngine(input, options, defects);
        return new OggAsyncDemuxer(await EngineHost.OpenAsync(engine, owned, cancellationToken).ConfigureAwait(false), owned);
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Ogg;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _engine.Tracks;

    /// <summary>The length of the media, or unknown when the format does not state it.</summary>
    public MediaTime Duration => _engine.Duration;

    /// <inheritdoc />
    public DefectLog Defects => _engine.Defects;

    /// <inheritdoc />
    public bool CanSeek => _engine.CanSeek;

    /// <inheritdoc />
    public PacketView Current => _engine.Current;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _engine.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default) => _engine.NextAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default) => _engine.SeekAsync(target, mode, cancellationToken);

    /// <inheritdoc />
    public ValueTask<MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default) => new(_engine.ReadMetadata());

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _engine.Dispose();
        if (_owned is not null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Opens Ogg files for the format registry.</summary>
public sealed class OggFormat : IMediaFormat, IHeadSeededFormat
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Ogg;

    /// <inheritdoc />
    public string DisplayName => "Ogg";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        // RFC 3533 §6: capture_pattern 'OggS', then stream_structure_version, which is 0. An ID3v2 tag may come first (ID3v2 §3.1).
        long skip = OggEngine.Id3Size(head);
        if (skip > 0)
        {
            return skip + 5 <= head.Length && head.Slice((int)skip, 4).SequenceEqual("OggS"u8) && head[(int)skip + 4] == 0 ? new SniffResult(SniffConfidence.Certain, "id3") : SniffResult.None;
        }

        return head.Length >= 5 && head[..4].SequenceEqual("OggS"u8) && head[4] == 0 ? new SniffResult(SniffConfidence.Certain) : SniffResult.None;
    }

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => OggDemuxer.Open(source, options, cancellationToken);

    IDemuxer IHeadSeededFormat.Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken) =>
        OggDemuxer.Open(source, head, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => OggDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await OggAsyncDemuxer.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
}
