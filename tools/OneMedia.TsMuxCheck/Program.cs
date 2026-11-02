using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.H264;
using RecursiveAI.OneMedia.Codecs.Hevc;
using RecursiveAI.OneMedia.Remuxing;

// Usage: TsMuxCheck <in> <out> [--m2ts]
// Remuxes <in> to a transport stream (Remuxer, the happy path's engine), reads both files with this library's readers and compares, per written
// track: packet count, keyframe flags, presentation and decode times (exact, after the 90 kHz rounding, up to one constant per track) and the
// payload after the reverse repackaging (NAL units without delimiters and parameter sets, AAC without the ADTS header). One summary line.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: TsMuxCheck <in> <out> [--m2ts]");
    return 2;
}

string input = args[0], output = args[1];
RemuxFormat format = args.Length > 2 && args[2] == "--m2ts" ? RemuxFormat.M2ts : RemuxFormat.MpegTs;
string name = Path.GetFileName(input);
try
{
    Stopwatch sw = Stopwatch.StartNew();
    RemuxReport report = Remuxer.Remux(input, output, format, new RemuxOptions { Overwrite = true, CarryTags = false });
    long ms = sw.ElapsedMilliseconds;
    using MediaFile a = Media.Open(input);
    using MediaFile b = Media.Open(output);
    List<Rec>[] inp = Collect(a, null);
    int[] map = new int[a.Tracks.Count];
    Array.Fill(map, -1);
    foreach (RemuxTrackReport t in report.Tracks)
    {
        if (t.OutputTrack >= 0 && t.InputTrack < map.Length)
        {
            map[t.InputTrack] = t.OutputTrack;
        }
    }

    List<Rec>[] outp = Collect(b, null);
    List<string> problems = [];
    long compared = 0;
    for (int i = 0; i < map.Length; i++)
    {
        if (map[i] < 0)
        {
            continue;
        }

        if (map[i] >= outp.Length)
        {
            problems.Add($"track {i}: no output track {map[i]}");
            continue;
        }

        List<Rec> x = inp[i], y = outp[map[i]];
        if (x.Count != y.Count)
        {
            problems.Add($"track {i} ({a.Tracks[i].Codec}): {x.Count} packets in, {y.Count} out");
        }

        int n = Math.Min(x.Count, y.Count);
        int badKey = 0, badPts = 0, badDts = 0, badData = 0;
        for (int k = 0; k < n; k++)
        {
            compared++;
            if (x[k].Key != y[k].Key) { badKey++; }
            // One tick of slack: a track moved by an edit start is rounded once as a whole, the input per packet.
            if (Math.Abs((x[k].Pts - x[0].Pts) - (y[k].Pts - y[0].Pts)) > 1) { badPts++; }
            if (x[0].Dts != long.MinValue && Math.Abs((x[k].Dts - x[0].Dts) - (y[k].Dts - y[0].Dts)) > 1) { badDts++; }
            if (x[k].Hash != y[k].Hash) { badData++; }
        }

        if (badKey + badPts + badDts + badData > 0)
        {
            problems.Add($"track {i} ({a.Tracks[i].Codec}): key {badKey}, pts {badPts}, dts {badDts}, data {badData} of {n}");
        }
    }

    Console.WriteLine($"{name}\t{(problems.Count == 0 ? "OK" : "DIFF")}\tpkts={compared}\tms={ms}\tpeakRssMB={Process.GetCurrentProcess().PeakWorkingSet64 >> 20}\t{string.Join("; ", problems)}");
    return 0;
}
catch (MediaException e)
{
    Console.WriteLine($"{name}\tREFUSED\t{e.GetType().Name}: {e.Message.Replace('\n', ' ')}");
    return 0;
}

static List<Rec>[] Collect(MediaFile f, object? unused)
{
    _ = unused;
    List<Rec>[] lists = new List<Rec>[f.Tracks.Count];
    NalCodec?[] nal = new NalCodec?[f.Tracks.Count];
    int[] lengthSize = new int[f.Tracks.Count];
    for (int i = 0; i < lists.Length; i++)
    {
        lists[i] = [];
        MediaTrack t = f.Tracks[i];
        if (t.Codec == CodecId.H264 || t.Codec == CodecId.Hevc)
        {
            nal[i] = t.Codec == CodecId.H264 ? NalCodec.H264 : NalCodec.Hevc;
            if (t.Configuration.Format == "avcC" && AvcDecoderConfigurationRecord.TryParse(t.Configuration.Data, out AvcDecoderConfigurationRecord? avc) && avc is not null)
            {
                lengthSize[i] = avc.LengthSize;
            }
            else if (t.Configuration.Format == "hvcC" && HevcDecoderConfigurationRecord.TryParse(t.Configuration.Data, out HevcDecoderConfigurationRecord? hevc) && hevc is not null)
            {
                lengthSize[i] = hevc.LengthSize;
            }
        }
    }

    IDemuxer d = f.Demuxer!;
    while (d.TryReadPacket(out PacketView v))
    {
        int i = v.Info.TrackIndex;
        if ((uint)i >= (uint)lists.Length)
        {
            continue;
        }

        ReadOnlySpan<byte> data = v.Data;
        using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (nal[i] is { } codec)
        {
            byte[] len = new byte[4];
            void Add(ReadOnlySpan<byte> unit)
            {
                if (unit.Length == 0 || NalUnit.GetType(codec, unit) == (codec == NalCodec.H264 ? 9 : 35) || NalUnit.IsParameterSet(codec, NalUnit.GetType(codec, unit)))
                {
                    return;
                }

                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, unit.Length);
                h.AppendData(len);
                h.AppendData(unit);
            }

            if (lengthSize[i] > 0)
            {
                int pos = 0;
                while (pos + lengthSize[i] <= data.Length)
                {
                    int n = 0;
                    for (int k = 0; k < lengthSize[i]; k++)
                    {
                        n = (n << 8) | data[pos + k];
                    }

                    pos += lengthSize[i];
                    if (n > data.Length - pos)
                    {
                        break;
                    }

                    Add(data.Slice(pos, n));
                    pos += n;
                }
            }
            else
            {
                AnnexBReader r = new(data);
                while (r.TryRead(out ReadOnlySpan<byte> unit, out _, out _))
                {
                    Add(unit);
                }
            }
        }
        else if (f.Tracks[i].Codec == CodecId.Aac && AdtsHeader.TryParse(data, out AdtsHeader hdr) && hdr.FrameLength == data.Length)
        {
            h.AppendData(data[hdr.HeaderSize..]);
        }
        else
        {
            h.AppendData(data);
        }

        long pts = v.Info.Pts.IsKnown ? v.Info.Pts.Rescale(90000, MediaRounding.NearestAwayFromZero).Ticks : long.MinValue;
        long dts = v.Info.Dts.IsKnown ? v.Info.Dts.Rescale(90000, MediaRounding.NearestAwayFromZero).Ticks : long.MinValue; // Matroska stores no decode times; the muxer derives them
        byte[] hash = h.GetHashAndReset();
        lists[i].Add(new Rec(pts, dts, v.Info.IsKeyframe, BitConverter.ToUInt64(hash, 0)));
    }

    return lists;
}

internal readonly record struct Rec(long Pts, long Dts, bool Key, ulong Hash);
