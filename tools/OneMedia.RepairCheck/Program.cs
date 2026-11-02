using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Repair;

// onemedia repair check: "repair <in> <out>" repairs and prints the report; "diff <original> <repaired>" compares packets per track
// (the repaired file must hold the original's first N packets of each track, equal in size, payload hash, times and keyframe flag).
if (args.Length >= 3 && args[0] == "repair")
{
    RepairReport r = Media.Repair(args[1], args[2]);
    Console.WriteLine($"status={r.Status} format={r.Format} in={r.InputLength} out={r.OutputLength} confidence={r.Confidence} dropped={r.DroppedBytes}");
    foreach (RepairItem i in r.Items)
    {
        Console.WriteLine($"  {i.Action} {i.Structure} track={i.Track} @{i.Range.Offset}+{i.Range.Length} [{i.Confidence}] {i.Detail}");
    }

    foreach (RepairTrackReport t in r.Tracks)
    {
        Console.WriteLine($"  track {t.Track} {t.Codec} packets={t.Packets} dropped={t.DroppedPackets} [{t.Confidence}]");
    }

    foreach (string w in r.Warnings)
    {
        Console.WriteLine("  warning: " + w);
    }

    return 0;
}

if (args.Length >= 3 && args[0] == "diff")
{
    Dictionary<int, List<(int Size, string Hash, string Pts, string Dts, bool Key)>> a = Load(args[1]);
    Dictionary<int, List<(int Size, string Hash, string Pts, string Dts, bool Key)>> b = Load(args[2]);
    int bad = 0;
    long matched = 0;
    foreach (KeyValuePair<int, List<(int Size, string Hash, string Pts, string Dts, bool Key)>> kv in a)
    {
        List<(int Size, string Hash, string Pts, string Dts, bool Key)> x = kv.Value;
        List<(int Size, string Hash, string Pts, string Dts, bool Key)> y = b.TryGetValue(kv.Key, out var yy) ? yy : [];
        int n = Math.Min(x.Count, y.Count);
        int mism = 0;
        for (int i = 0; i < n; i++)
        {
            if (x[i] != y[i])
            {
                if (mism++ < 3)
                {
                    Console.WriteLine($"  track {kv.Key} packet {i}: {x[i]} != {y[i]}");
                }
            }
        }

        matched += n - mism;
        bad += mism;
        Console.WriteLine($"track {kv.Key}: original={x.Count} repaired={y.Count} compared={n} mismatches={mism}");
    }

    foreach (int t in b.Keys)
    {
        if (!a.ContainsKey(t))
        {
            Console.WriteLine($"track {t}: only in repaired");
            bad++;
        }
    }

    Console.WriteLine($"RESULT matched={matched} mismatches={bad}");
    return bad == 0 ? 0 : 1;
}

if (args.Length >= 2 && args[0] == "count")
{
    Dictionary<int, List<(int Size, string Hash, string Pts, string Dts, bool Key)>> a = Load(args[1]);
    foreach (KeyValuePair<int, List<(int Size, string Hash, string Pts, string Dts, bool Key)>> kv in a)
    {
        Console.WriteLine($"track {kv.Key}: {kv.Value.Count}");
    }

    return 0;
}

Console.Error.WriteLine("usage: RepairCheck repair <in> <out> | diff <original> <repaired> | count <file>");
return 2;

static Dictionary<int, List<(int Size, string Hash, string Pts, string Dts, bool Key)>> Load(string path)
{
    var result = new Dictionary<int, List<(int Size, string Hash, string Pts, string Dts, bool Key)>>();
    using MediaFile f = Media.Open(path);
    IDemuxer d = f.Demuxer!;
    while (d.TryReadPacket(out PacketView v))
    {
        if (!result.TryGetValue(v.Info.TrackIndex, out var list))
        {
            result[v.Info.TrackIndex] = list = [];
        }

        list.Add((v.Info.Size, Convert.ToHexString(SHA256.HashData(v.Data))[..16], v.Info.Pts.ToString(), v.Info.Dts.ToString(), v.Info.IsKeyframe));
    }

    return result;
}
