using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using Atl = ATL;
using Oa = global::OneAudio;
using OaMp4 = global::OneAudio.Mp4;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>What one run of a row's work produced; the same counters are reported for every implementation so mismatches show.</summary>
/// <param name="Packets">Packets (samples, blocks) read.</param>
/// <param name="Bytes">Payload bytes of those packets.</param>
/// <param name="Aux">Row-specific count: tracks for a probe, tags where the implementation reports them.</param>
internal readonly record struct Work(long Packets, long Bytes, long Aux = 0);

/// <summary>Raised by an implementation that cannot run in this build (its reader is not registered yet).</summary>
internal sealed class NotAvailableException(string message) : Exception(message);

/// <summary>The role an implementation plays in a row.</summary>
internal enum Role
{
    /// <summary>OneMedia itself.</summary>
    Ours,

    /// <summary>A managed library the bar is measured against.</summary>
    Gate,

    /// <summary>OneMedia as it was before a change, kept beside the current figure; shown with a ratio but never counted in the bar.</summary>
    Previous,
}

internal delegate Work RunFn(string path);

internal sealed record Impl(string Name, Role Role, RunFn Run);

/// <summary>
/// The work each implementation does for a row. Every comparator is called the way its published README shows; where the README
/// does not cover a row the obvious public call is used and the choice is written in docs/bench.md.
/// </summary>
internal static class Impls
{
    private const int BufferSize = 64 * 1024;

    private static FileStream OpenStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

    // ---- OneMedia ---------------------------------------------------------------------------------------------------------

    /// <summary>The registry every caller of <c>Media.Open</c> gets, so the rows pay what a user pays to find the format.</summary>
    public static MediaFormatRegistry? OurRegistry() => MediaFormats.Default;

    private static readonly Lazy<MediaFormatRegistry?> Registry = new(OurRegistry);

    private static IDemuxer OpenOurs(string path)
    {
        MediaFormatRegistry registry = Registry.Value ?? throw new NotAvailableException("no OneMedia reader is registered in this build");
        return registry.Open(FileMediaSource.Open(path));
    }

    /// <summary>
    /// The file source as it behaved before it remembered its length: every call to <see cref="Length"/> asks the file system. Used to show
    /// the effect of that change in one process, on the same files.
    /// </summary>
    private sealed class PerCallLengthSource : IMediaSource
    {
        private readonly FileMediaSource _inner;
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;

        public PerCallLengthSource(string path)
        {
            _inner = FileMediaSource.Open(path);
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        }

        public long Length => RandomAccess.GetLength(_handle);

        public int Read(long offset, Span<byte> destination, System.Threading.CancellationToken cancellationToken = default) =>
            _inner.Read(offset, destination, cancellationToken);

        public void Dispose()
        {
            _inner.Dispose();
            _handle.Dispose();
        }
    }

    private static IDemuxer OpenOursPerCallLength(string path)
    {
        MediaFormatRegistry registry = Registry.Value ?? throw new NotAvailableException("no OneMedia reader is registered in this build");
        return registry.Open(new PerCallLengthSource(path));
    }

    public static Work OursDemuxPerCallLength(string path)
    {
        using IDemuxer d = OpenOursPerCallLength(path);
        return Drain(d);
    }

    public static Work OursAudioPerCallLength(string path)
    {
        using IDemuxer d = OpenOursPerCallLength(path);
        SelectFirstAudio(d);
        return Drain(d);
    }

    private static void SelectFirstAudio(IDemuxer d)
    {
        int audio = -1;
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            if (d.Tracks[i].Kind == TrackKind.Audio && audio < 0)
            {
                audio = i;
            }
            else
            {
                d.SetTrackEnabled(i, false);
            }
        }

        if (audio < 0)
        {
            throw new InvalidOperationException("no audio track");
        }
    }

    public static Work OursDemux(string path)
    {
        using IDemuxer d = OpenOurs(path);
        return Drain(d);
    }

    /// <summary>Audio track only: every other track is switched off, as a caller reading one stream would do.</summary>
    public static Work OursAudio(string path)
    {
        using IDemuxer d = OpenOurs(path);
        int audio = -1;
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            bool isAudio = d.Tracks[i].Kind == TrackKind.Audio;
            if (isAudio && audio < 0)
            {
                audio = i;
            }
            else
            {
                d.SetTrackEnabled(i, false);
            }
        }

        if (audio < 0)
        {
            throw new InvalidOperationException("no audio track");
        }

        return Drain(d);
    }

    /// <summary>Copy-mux every packet into Matroska or WebM (WebM for a .webm input) on a stream that keeps nothing but can seek, so the figure is the muxer and the reader.</summary>
    public static Work OursMuxMatroska(string path)
    {
        using IDemuxer d = OpenOurs(path);
        MatroskaMuxOptions options = new() { Profile = path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? MatroskaProfile.WebM : MatroskaProfile.Matroska };
        using CountingSeekableSink sink = new();
        using MatroskaMuxer mux = new(sink, options);
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            try
            {
                mux.AddTrack(d.Tracks[i]);
            }
            catch (MuxRefusedException)
            {
                throw new NotAvailableException("a track the muxer cannot carry");
            }
        }

        long packets = 0, bytes = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            mux.WritePacket(p.Info, p.Data);
            packets++;
            bytes += p.Data.Length;
        }

        MuxReport report = mux.Finish();
        return new Work(packets, bytes, report.BytesWritten & 1);
    }

    private sealed class CountingSeekableSink : Stream
    {
        private long _position;
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _position; set => _position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => _position = origin == SeekOrigin.Begin ? offset : throw new NotSupportedException();

        public override void SetLength(long value) => _length = value;

        public override void Write(byte[] buffer, int offset, int count) => Advance(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Advance(buffer.Length);

        private void Advance(int count)
        {
            _position += count;
            _length = Math.Max(_length, _position);
        }
    }

    private static Work Drain(IDemuxer d)
    {
        long packets = 0, bytes = 0, touch = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            packets++;
            ReadOnlySpan<byte> data = p.Data;
            bytes += data.Length;
            if (data.Length > 0)
            {
                touch += data[0] ^ data[^1];
            }
        }

        return new Work(packets, bytes, touch & 1);
    }

    /// <summary>
    /// Bytes the pull path allocates per packet once warm: reads the first half of the packets, then measures the second half on the
    /// same demuxer. A file with the movie box at the end or an all-track pull both go through the same call.
    /// </summary>
    public static (long Packets, long Bytes) PullAllocation(string path)
    {
        long total = OursDemux(path).Packets;
        using IDemuxer d = OpenOurs(path);
        if (d.CanSeek)
        {
            // Steady state: a whole first pass lets index lists and buffers reach their size; the second pass from the start is measured.
            while (d.TryReadPacket(out _))
            {
            }

            d.Seek(MediaTime.Zero);
            long all = 0;
            long start = GC.GetAllocatedBytesForCurrentThread();
            while (d.TryReadPacket(out _))
            {
                all++;
            }

            return (all, GC.GetAllocatedBytesForCurrentThread() - start);
        }

        long half = total / 2;
        long n = 0;
        while (n < half && d.TryReadPacket(out _))
        {
            n++;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long measured = 0;
        while (d.TryReadPacket(out _))
        {
            measured++;
        }

        return (measured, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    public static Work OursProbe(string path)
    {
        using IDemuxer d = OpenOurs(path);
        long tracks = d.Tracks.Count;
        _ = d.Duration;
        return new Work(0, 0, tracks);
    }

    /// <summary>Opens the file and reads its tags through the common view: title, artist, album, genre, the picture count and every native tag's text.</summary>
    public static Work OursTags(string path)
    {
        using IDemuxer d = OpenOurs(path);
        RecursiveAI.OneMedia.Metadata.MediaMetadata m = ((RecursiveAI.OneMedia.Metadata.IMetadataProvider)d).ReadMetadata();
        long n = 0;
        foreach (RecursiveAI.OneMedia.Tags.NativeTag t in m.NativeTags)
        {
            _ = t.Text;
            n++;
        }

        _ = m.Tags.Title;
        _ = m.Tags.Artist;
        _ = m.Tags.Album;
        _ = m.Tags.Genre;
        return new Work(0, 0, n + m.Tags.Pictures.Count);
    }

    public static Work TagLibTags(string path)
    {
        using TagLib.File f = TagLib.File.Create(path);
        _ = f.Tag.Title;
        _ = f.Tag.FirstPerformer;
        _ = f.Tag.Album;
        _ = f.Tag.FirstGenre;
        return new Work(0, 0, f.Tag.Pictures.Length);
    }

    public static Work AtlTags(string path)
    {
        Atl.Track t = new(path);
        _ = t.Title;
        _ = t.Artist;
        _ = t.Album;
        _ = t.Genre;
        return new Work(0, 0, t.AdditionalFields.Count + t.EmbeddedPictures.Count);
    }

    // ---- MP4 mux of the first AAC track -------------------------------------------------------------------------------------

    private sealed record AacInput(MediaTrack Track, byte[][] Frames, long Ticks);

    private static readonly Dictionary<string, AacInput> AacInputs = [];

    // The packets are read once per process, outside the measured work: the row times the muxers, with the same frames in memory for each.
    private static AacInput LoadAac(string path)
    {
        lock (AacInputs)
        {
            if (AacInputs.TryGetValue(path, out AacInput? known))
            {
                return known;
            }

            using IDemuxer d = OpenOurs(path);
            SelectFirstAudio(d);
            MediaTrack track = d.Tracks.First(t => t.Kind == TrackKind.Audio);
            List<byte[]> frames = [];
            while (d.TryReadPacket(out PacketView p))
            {
                frames.Add(p.Data.ToArray());
            }

            return AacInputs[path] = new AacInput(track, [.. frames], 0);
        }
    }

    private static Work MuxAac(string path, RecursiveAI.OneMedia.IsoBmff.IsoLayout layout)
    {
        AacInput input = LoadAac(path);
        using MemoryStream output = new(1 << 20);
        using RecursiveAI.OneMedia.IsoBmff.IsoMuxer mux = new(output, new RecursiveAI.OneMedia.IsoBmff.IsoMuxOptions { Layout = layout });
        int track = mux.AddTrack(input.Track with { Edit = default });
        long dts = 0;
        long timescale = input.Track.Timescale;
        foreach (byte[] frame in input.Frames)
        {
            MediaTime t = new(dts, timescale);
            mux.WritePacket(new PacketInfo(track, t, t, new MediaTime(1024, timescale), PacketAttributes.Keyframe, ByteRange.None, frame.Length), frame);
            dts += 1024;
        }

        mux.Finish();
        return new Work(input.Frames.Length, output.Length);
    }

    public static Work OursMuxAac(string path) => MuxAac(path, RecursiveAI.OneMedia.IsoBmff.IsoLayout.MoovFirst);

    public static Work OursMuxAacMoovLast(string path) => MuxAac(path, RecursiveAI.OneMedia.IsoBmff.IsoLayout.MoovLast);

    public static Work SharpMp4MuxAac(string path)
    {
        AacInput input = LoadAac(path);
        using MemoryStream output = new(1 << 20);
        SharpMP4.Builders.Mp4Builder builder = new SharpMP4.Builders.Mp4Builder(new SharpMP4.Builders.SingleStreamOutput(output));
        SharpMP4.Tracks.AACTrack track = new((byte)input.Track.Audio!.Value.Channels, (uint)input.Track.Audio!.Value.SampleRate, 16);
        builder.AddTrack(track);
        foreach (byte[] frame in input.Frames)
        {
            builder.ProcessTrackSample(track.TrackID, frame, 1024);
        }

        builder.FinalizeMedia();
        return new Work(input.Frames.Length, output.Length);
    }

    // ---- MP4 remux of the first AAC track: read the file and write it again --------------------------------------------------

    public static Work OursRemuxAac(string path)
    {
        using MediaFile file = Media.Open(path);
        int audio = file.Audio!.Index;
        using MemoryStream output = new(1 << 20);
        RecursiveAI.OneMedia.Remuxing.RemuxReport report = RecursiveAI.OneMedia.Remuxing.Remuxer.Remux(file, output, RecursiveAI.OneMedia.Remuxing.RemuxFormat.Mp4,
            new RecursiveAI.OneMedia.Remuxing.RemuxOptions { Select = t => t.Index == audio });
        return new Work(report.Tracks.Sum(t => t.Packets), output.Length);
    }

    public static Work SharpMp4RemuxAac(string path)
    {
        // The stream parameters come from the same description for both rows; the samples are read by SharpMP4's own reader.
        AacInput info = LoadAac(path);
        using FileStream fs = OpenStream(path);
        SharpISOBMFF.Container mp4 = new();
        mp4.Read(new SharpISOBMFF.IsoStream(fs));
        SharpMP4.Readers.VideoReader reader = new();
        reader.Parse(mp4);
        using MemoryStream output = new(1 << 20);
        SharpMP4.Builders.Mp4Builder builder = new(new SharpMP4.Builders.SingleStreamOutput(output));
        SharpMP4.Tracks.AACTrack track = new((byte)info.Track.Audio!.Value.Channels, (uint)info.Track.Audio!.Value.SampleRate, 16);
        builder.AddTrack(track);
        long packets = 0;
        foreach (SharpMP4.Tracks.ITrack source in reader.GetTracks().ToList())
        {
            if (source is not SharpMP4.Tracks.AACTrack)
            {
                continue;
            }

            SharpMP4.Readers.MediaSample? s;
            while ((s = reader.ReadSample(source.TrackID)) is not null)
            {
                builder.ProcessTrackSample(track.TrackID, s.Data, 1024);
                packets++;
            }

            break;
        }

        builder.FinalizeMedia();
        return new Work(packets, output.Length);
    }

    // ---- SharpMP4 ---------------------------------------------------------------------------------------------------------

    public static Work SharpMp4Demux(string path)
    {
        using FileStream fs = OpenStream(path);
        SharpISOBMFF.Container mp4 = new();
        mp4.Read(new SharpISOBMFF.IsoStream(fs));
        SharpMP4.Readers.VideoReader reader = new();
        reader.Parse(mp4);
        long packets = 0, bytes = 0, touch = 0;
        foreach (SharpMP4.Tracks.ITrack track in reader.GetTracks().ToList())
        {
            SharpMP4.Readers.MediaSample? s;
            while ((s = reader.ReadSample(track.TrackID)) is not null)
            {
                packets++;
                byte[] data = s.Data;
                bytes += data.Length;
                if (data.Length > 0)
                {
                    touch += data[0] ^ data[^1];
                }
            }
        }

        return new Work(packets, bytes, touch & 1);
    }

    // ---- OneAudio ---------------------------------------------------------------------------------------------------------

    public static Work OneAudioMp4(string path)
    {
        using FileStream fs = OpenStream(path);
        using OaMp4.Mp4PacketSource src = OaMp4.Mp4PacketSource.Open(fs);
        long packets = 0, bytes = 0, touch = 0;
        while (src.TryReadPacket(out Oa.PacketView p))
        {
            packets++;
            ReadOnlySpan<byte> data = p.Data;
            bytes += data.Length;
            if (data.Length > 0)
            {
                touch += data[0] ^ data[^1];
            }
        }

        return new Work(packets, bytes, touch & 1);
    }

    // ---- Probe/metadata comparators ---------------------------------------------------------------------------------------

    public static Work MetadataExtractorProbe(string path)
    {
        using FileStream fs = OpenStream(path);
        IReadOnlyList<MetadataExtractor.Directory> dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(fs);
        long tags = 0;
        foreach (MetadataExtractor.Directory dir in dirs)
        {
            foreach (MetadataExtractor.Tag tag in dir.Tags)
            {
                _ = tag.Description;
                tags++;
            }
        }

        return new Work(0, 0, tags);
    }

    public static Work TagLibProbe(string path)
    {
        using TagLib.File f = TagLib.File.Create(path);
        _ = f.Properties.Duration;
        _ = f.Tag.Title;
        _ = f.Tag.FirstPerformer;
        return new Work(0, 0, f.Properties.Codecs.Count());
    }

    public static Work AtlProbe(string path)
    {
        Atl.Track t = new(path);
        _ = t.Duration;
        _ = t.Title;
        _ = t.Artist;
        return new Work(0, 0, t.AdditionalFields.Count);
    }

    // ---- Tag reads of audio files ------------------------------------------------------------------------------------------

    // The same eight fields from every reader: what a library user asks of a tagged MP3.
    public static Work Mp3OursTags(string path)
    {
        using IDemuxer d = OpenOurs(path);
        MediaMetadata m = ((IMetadataProvider)d).ReadMetadata();
        long n = 0;
        foreach (NativeTag t in m.NativeTags)
        {
            _ = t.Text;
            n++;
        }

        CommonTags c = m.Tags;
        _ = c.Title;
        _ = c.Artist;
        _ = c.Album;
        _ = c.Date;
        _ = c.Comment;
        _ = c.Genre;
        _ = c.TrackNumber;
        return new Work(0, 0, n);
    }

    public static Work Mp3TagLibTags(string path)
    {
        using TagLib.File f = TagLib.File.Create(path);
        TagLib.Tag t = f.Tag;
        _ = t.Title;
        _ = t.FirstPerformer;
        _ = t.Album;
        _ = t.Year;
        _ = t.Comment;
        _ = t.FirstGenre;
        _ = t.Track;
        return new Work(0, 0, t.Pictures.Length);
    }

    public static Work Mp3AtlTags(string path)
    {
        Atl.Track t = new(path);
        _ = t.Title;
        _ = t.Artist;
        _ = t.Album;
        _ = t.Year;
        _ = t.Comment;
        _ = t.Genre;
        _ = t.TrackNumber;
        return new Work(0, 0, t.AdditionalFields.Count);
    }

    // ---- Matroska comparators ---------------------------------------------------------------------------------------------

    public static Work StefHMatroskaDemux(string path)
    {
        using FileStream fs = OpenStream(path);
        global::Matroska.Models.MatroskaDocument doc = global::Matroska.MatroskaSerializer.Deserialize(fs);
        long packets = 0, bytes = 0;
        foreach (global::Matroska.Models.Cluster c in doc.Segment?.Clusters ?? [])
        {
            foreach (global::Matroska.Models.SimpleBlock b in c.SimpleBlocks ?? [])
            {
                packets++;
                bytes += b.Data?.Length ?? 0;
            }

            foreach (global::Matroska.Models.BlockGroup g in c.BlockGroups ?? [])
            {
                foreach (global::Matroska.Models.Block b in g.Blocks ?? [])
                {
                    packets++;
                    bytes += b.Data?.Length ?? 0;
                }
            }
        }

        return new Work(packets, bytes, doc.Segment?.Tracks?.TrackEntries?.Count ?? 0);
    }

    // EBML ids written as the spec prints them, marker bits included.
    private const ulong IdSegment = 0x18538067, IdInfo = 0x1549A966, IdTracks = 0x1654AE6B, IdCluster = 0x1F43B675;
    private const ulong IdTrackEntry = 0xAE, IdTrackType = 0x83, IdCodecId = 0x86, IdTimestampScale = 0x2AD7B1, IdDuration = 0x4489;

    /// <summary>
    /// Structure-only probe on NEbml's element reader (the reader inside the Matroska package): Info and Tracks are read, clusters
    /// are skipped, and the walk stops at the first cluster. This is the cheapest probe the package's public API allows.
    /// </summary>
    public static Work NEbmlProbe(string path)
    {
        using FileStream fs = OpenStream(path);
        using NEbml.Core.EbmlReader r = new(fs);
        long tracks = 0;
        double duration = 0;
        ulong scale = 1_000_000;
        bool done = false;
        while (!done && r.ReadNext())
        {
            if (r.ElementId.EncodedValue != IdSegment)
            {
                continue;
            }

            r.EnterContainer();
            while (!done && r.ReadNext())
            {
                ulong id = r.ElementId.EncodedValue;
                if (id == IdInfo)
                {
                    r.EnterContainer();
                    while (r.ReadNext())
                    {
                        ulong child = r.ElementId.EncodedValue;
                        if (child == IdTimestampScale)
                        {
                            scale = r.ReadUInt();
                        }
                        else if (child == IdDuration)
                        {
                            duration = r.ReadFloat();
                        }
                    }

                    r.LeaveContainer();
                }
                else if (id == IdTracks)
                {
                    r.EnterContainer();
                    while (r.ReadNext())
                    {
                        if (r.ElementId.EncodedValue != IdTrackEntry)
                        {
                            continue;
                        }

                        r.EnterContainer();
                        while (r.ReadNext())
                        {
                            ulong child = r.ElementId.EncodedValue;
                            if (child == IdTrackType)
                            {
                                _ = r.ReadUInt();
                            }
                            else if (child == IdCodecId)
                            {
                                _ = r.ReadAscii();
                            }
                        }

                        r.LeaveContainer();
                        tracks++;
                    }

                    r.LeaveContainer();
                }
                else if (id == IdCluster)
                {
                    done = true;
                }
            }
        }

        _ = duration * scale; // the segment duration in nanoseconds, computed as a probe would report it
        return new Work(0, 0, tracks);
    }
}
