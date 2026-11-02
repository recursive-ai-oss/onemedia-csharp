using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

// Usage: DateWriteCheck <in> <out> <iso8601 with offset> [utc|local]
// One line on stdout: DATE <tab> <in> <tab> JSON, or ERROR <tab> <in> <tab> message.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: DateWriteCheck <in> <out> <iso8601> [utc|local]");
    return 2;
}

try
{
    DateTimeOffset when = DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture);
    bool? local = args.Length > 3 ? args[3] == "local" : null;
    string before = Hash(args[0]);
    CaptureTimeReport r = CaptureTimeWriter.SaveAs(args[0], args[1], when, new CaptureTimeOptions { HeaderTimesAreLocal = local }, overwrite: true);
    string after = Hash(args[1]);
    using MediaFile m = Media.Open(args[1]);
    FactCandidate<MediaDateTime>? best = m.CaptureTime.Best;
    string resolved = best is null ? "none" : best.Value.ToString();
    // The resolver's answer is the written one when it is the same instant (zone known) or the same wall clock (zone unknown).
    bool ok = best is not null && (best.Value.ToDateTimeOffset() is { } i ? Math.Abs((i - when).TotalSeconds) < 1 : Math.Abs((best.Value.Value - when.DateTime).TotalSeconds) < 1);

    using MemoryStream ms = new();
    using (Utf8JsonWriter w = new(ms))
    {
        w.WriteStartObject();
        w.WriteString("when", when.ToString("o", CultureInfo.InvariantCulture));
        w.WriteString("headerBasis", r.HeaderTimesBasis);
        w.WriteString("resolved", resolved);
        w.WriteString("resolvedSource", best?.Source ?? "");
        w.WriteBoolean("resolvedOk", ok);
        w.WriteBoolean("payloadEqual", before == after);
        w.WriteStartArray("written");
        foreach (DateFieldWrite f in r.Written)
        {
            w.WriteStartObject();
            w.WriteString("system", f.System);
            w.WriteString("field", f.Field);
            w.WriteString("old", f.OldValue);
            w.WriteString("new", f.NewValue);
            w.WriteString("semantics", f.Semantics);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("left");
        foreach (DateFieldLeft f in r.Left)
        {
            w.WriteStartObject();
            w.WriteString("system", f.System);
            w.WriteString("field", f.Field);
            w.WriteString("value", f.Value);
            w.WriteString("reason", f.Reason);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("disagreements");
        foreach (DateDisagreement d in r.Disagreements)
        {
            w.WriteStartObject();
            w.WriteString("source", d.Source);
            w.WriteString("raw", d.RawValue);
            w.WriteString("note", d.Note);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    Console.WriteLine("DATE\t" + args[0] + "\t" + Encoding.UTF8.GetString(ms.ToArray()));
    return 0;
}
catch (Exception e) when (e is MediaException or IOException or ArgumentException or FormatException)
{
    Console.WriteLine($"ERROR\t{args[0]}\t{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
    return 1;
}

// Per-track SHA-256 of the packet payloads read through the demuxer.
static string Hash(string path)
{
    using MediaFile m = Media.Open(path);
    IDemuxer d = m.Demuxer ?? throw new InvalidOperationException("no sync demuxer");
    Dictionary<int, IncrementalHash> hashes = [];
    try
    {
        while (d.TryReadPacket(out PacketView v))
        {
            if (!hashes.TryGetValue(v.Info.TrackIndex, out IncrementalHash? h))
            {
                hashes[v.Info.TrackIndex] = h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }

            h.AppendData(v.Data);
        }

        StringBuilder sb = new();
        foreach (int k in hashes.Keys.Order())
        {
            sb.Append(k).Append(':').Append(Convert.ToHexString(hashes[k].GetHashAndReset())).Append(' ');
        }

        return sb.ToString();
    }
    finally
    {
        foreach (IncrementalHash h in hashes.Values)
        {
            h.Dispose();
        }
    }
}
