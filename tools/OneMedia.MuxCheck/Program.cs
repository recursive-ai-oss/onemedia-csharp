using System.Security.Cryptography;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.IsoBmff;

// Usage:
//   MuxCheck copy <in> <out> [--kind auto|mp4|mov|3gp|3g2|m4a] [--layout first|last|frag] [--cmaf] [--sidx] [--mfra] [--no-seek]
//       Copies every track the muxer can carry from an ISO file to a new file and writes <out>.map (one line per output track:
//       "<output index> <input index>"). Prints one summary line: packets, bytes, skipped tracks, notes, peak resident memory.
//   MuxCheck alloc <in> [--layout first|last|frag]
//       Muxes the file into a null stream twice and prints the bytes allocated by the thread during the second run, per packet.
// The output is then compared with ffprobe by tools/run-mux-diff.sh.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: MuxCheck copy <in> <out> [options] | MuxCheck alloc <in> [options]");
    return 2;
}

string mode = args[0];
string input = args[1];
string? output = mode == "copy" ? args[2] : null;
string kindName = "auto";
string layoutName = "first";
bool cmaf = false, sidx = false, mfra = false, noSeek = false;
for (int i = mode == "copy" ? 3 : 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--kind": kindName = args[++i]; break;
        case "--layout": layoutName = args[++i]; break;
        case "--cmaf": cmaf = true; break;
        case "--sidx": sidx = true; break;
        case "--mfra": mfra = true; break;
        case "--no-seek": noSeek = true; break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

HashSet<string> carried = ["colr", "clap", "fiel", "gama", "btrt", "chnl", "srat", "mdcv", "clli"];
return mode switch
{
    "copy" => Guard(() => Copy(input, output!)),
    "alloc" => Alloc(input),
    _ => 2,
};

IsoMuxOptions Options(IsoFileKind kind) => new()
{
    Kind = kind,
    Layout = layoutName switch { "last" => IsoLayout.MoovLast, "frag" => IsoLayout.Fragmented, _ => IsoLayout.MoovFirst },
    Cmaf = cmaf,
    WriteSegmentIndex = sidx,
    WriteRandomAccess = mfra,
};

IsoFileKind KindOf(FileTypeBox? ft)
{
    if (kindName != "auto")
    {
        return kindName switch { "mov" => IsoFileKind.Mov, "3gp" => IsoFileKind.ThreeGp, "3g2" => IsoFileKind.ThreeG2, "m4a" => IsoFileKind.M4a, _ => IsoFileKind.Mp4 };
    }

    string major = ft?.MajorBrand ?? "isom";
    return major switch
    {
        "qt  " => IsoFileKind.Mov,
        "M4A " => IsoFileKind.M4a,
        _ when major.StartsWith("3gp", StringComparison.Ordinal) => IsoFileKind.ThreeGp,
        _ when major.StartsWith("3g2", StringComparison.Ordinal) => IsoFileKind.ThreeG2,
        _ => IsoFileKind.Mp4,
    };
}

IsoTrackOptions? TrackOptions(IsoTrack iso)
{
    if (iso.SampleEntries.Count == 0)
    {
        return null;
    }

    List<ReadOnlyMemory<byte>> boxes = [];
    foreach (ExtensionBox e in iso.SampleEntries[0].Extensions)
    {
        if (carried.Contains(e.TypeName))
        {
            boxes.Add(BoxBuilder.Create(e.Type, e.Payload.Span));
        }
    }

    return boxes.Count == 0 ? null : new IsoTrackOptions { SampleEntryBoxes = boxes };
}

int Guard(Func<int> run)
{
    // A file the reader or muxer refuses is a result, not a crash: one line says which and why.
    try
    {
        return run();
    }
    catch (MediaException e)
    {
        Console.WriteLine($"{Path.GetFileName(input)}\tREFUSED\t{e.GetType().Name}: {e.Message.Replace('\n', ' ')}");
        return 0;
    }
}

int Copy(string inPath, string outPath)
{
    Stopwatch sw = Stopwatch.StartNew();
    using FileMediaSource src = FileMediaSource.Open(inPath);
    using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src, new MediaOpenOptions { LeaveOpen = true });
    FileStream fs = new(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
    Stream dest = noSeek ? new NonSeekable(fs) : fs;
    long packets = 0;
    List<string> skipped = [];
    MuxReport report;
    using (IsoMuxer mux = new(dest, Options(KindOf(d.FileType))))
    {
        int[] map = new int[d.Tracks.Count];
        List<(int Out, int In)> mapping = [];
        // A CMAF track file holds one track: the first video track the muxer can carry, else the first track.
        IEnumerable<int> order = Enumerable.Range(0, map.Length);
        if (cmaf)
        {
            order = order.OrderBy(i => d.Tracks[i].Kind == TrackKind.Video ? 0 : 1);
        }

        foreach (int i in order)
        {
            map[i] = -1;
            MediaTrack t = d.Tracks[i];
            if (cmaf && mapping.Count > 0)
            {
                d.SetTrackEnabled(i, false);
                skipped.Add($"{i}:cmaf-single-track:{t.CodecTag}");
                continue;
            }

            try
            {
                map[i] = mux.AddTrack(t, TrackOptions(d.Movie.Tracks[i]));
                mapping.Add((map[i], i));
            }
            catch (MuxRefusedException e)
            {
                d.SetTrackEnabled(i, false);
                skipped.Add($"{i}:{e.Reason}:{t.CodecTag}");
            }
        }

        if (mapping.Count == 0)
        {
            fs.Dispose();
            File.Delete(outPath);
            Console.WriteLine($"{Path.GetFileName(inPath)}\tNO-TRACKS\tskipped=[{string.Join(' ', skipped)}]\ttracks={d.Tracks.Count}\tdefects={d.Defects.Count}");
            return 0;
        }

        File.WriteAllLines(outPath + ".map", mapping.Select(m => $"{m.Out} {m.In}"));
        // What the container reader delivered, for the comparison to tell a muxer change from a way ffprobe reads the input.
        using StreamWriter reader = new(outPath + ".pkts");
        while (d.TryReadPacket(out PacketView v))
        {
            PacketInfo info = v.Info with { TrackIndex = map[v.Info.TrackIndex] };
            mux.WritePacket(info, v.Data);
            if (packets < 3_000_000)
            {
                reader.WriteLine($"{info.TrackIndex} {info.Dts.Ticks} {info.Pts.Ticks} {(info.Duration.IsKnown ? info.Duration.Ticks : -1)} {v.Data.Length} {(info.IsKeyframe ? 1 : 0)} {((info.Flags & PacketAttributes.Disposable) != 0 ? 1 : 0)} {Convert.ToHexStringLower(SHA256.HashData(v.Data))}");
            }

            packets++;
        }

        report = mux.Finish();
    }

    fs.Dispose();
    long rss = PeakRss();
    Console.WriteLine($"{Path.GetFileName(inPath)}\tok\tpackets={packets}\tbytes={report.BytesWritten}\tskipped=[{string.Join(' ', skipped)}]\tnotes={report.Notes.Count}\tdefects={d.Defects.Count}\tms={sw.ElapsedMilliseconds}\tpeakRssMB={rss >> 20}");
    foreach (string n in report.Notes)
    {
        Console.WriteLine("\tnote: " + n);
    }

    return 0;
}

int Alloc(string inPath)
{
    // Reads the packets once into memory (outside the measured region), then muxes them twice into a null stream.
    List<(PacketInfo Info, byte[] Data)> all = [];
    IReadOnlyList<MediaTrack> tracks;
    IsoFileKind kind;
    using (FileMediaSource src = FileMediaSource.Open(inPath))
    using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(src, new MediaOpenOptions { LeaveOpen = true }))
    {
        tracks = d.Tracks;
        kind = KindOf(d.FileType);
        while (d.TryReadPacket(out PacketView v))
        {
            all.Add((v.Info, v.Data.ToArray()));
        }
    }

    long allocated = 0;
    for (int run = 0; run < 3; run++)
    {
        using IsoMuxer mux = new(Stream.Null, Options(kind) with { SpoolMemoryBudget = 1L << 30 });
        int[] map = new int[tracks.Count];
        for (int i = 0; i < map.Length; i++)
        {
            try
            {
                map[i] = mux.AddTrack(tracks[i]);
            }
            catch (MuxRefusedException)
            {
                map[i] = -1;
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long count = 0;
        foreach ((PacketInfo info, byte[] data) in all)
        {
            if (map[info.TrackIndex] < 0)
            {
                continue;
            }

            mux.WritePacket(info with { TrackIndex = map[info.TrackIndex] }, data);
            count++;
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        allocated = after - before;
        Console.WriteLine($"run {run}: packets={count} allocated={allocated} B ({(count == 0 ? 0 : (double)allocated / count):F3} B/packet)");
        mux.Finish();
    }

    return 0;
}

static long PeakRss()
{
    foreach (string line in File.ReadLines("/proc/self/status"))
    {
        if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
        {
            return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
        }
    }

    return 0;
}

/// <summary>Hides seeking so the muxer's streaming paths are used.</summary>
sealed class NonSeekable(Stream inner) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
}
