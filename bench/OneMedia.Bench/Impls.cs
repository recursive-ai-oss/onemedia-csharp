using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// The formats registered for the rows. When the ISO BMFF and Matroska readers exist, add them here, one line each.
    /// </summary>
    public static MediaFormatRegistry? OurRegistry()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new RecursiveAI.OneMedia.IsoBmff.IsoBmffFormat());
        registry.Add(new RecursiveAI.OneMedia.Matroska.MatroskaFormat());
        registry.Add(new RecursiveAI.OneMedia.Avi.AviFormat());
        registry.Add(new RecursiveAI.OneMedia.Dv.DvFormat());
        foreach (IMediaFormatSniffer s in registry.Formats)
        {
            if (s is IMediaFormat)
            {
                return registry;
            }
        }

        return null;
    }

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
