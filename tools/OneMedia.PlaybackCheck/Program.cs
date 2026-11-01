using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Playback;

// describe DIR MANIFEST.json            writes the probe manifest for every file in DIR
// verdicts DIR PROFILE                  prints our verdict per file for one profile
// compare MANIFEST RESULTS.jsonl PROFILE [TABLE.tsv]
//                                       compares our verdicts with the browser's observed playback; exit 1 on any disagreement
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: describe DIR MANIFEST | verdicts DIR PROFILE | compare MANIFEST RESULTS PROFILE [TABLE]");
    return 2;
}

switch (args[0])
{
    case "describe":
        return Describe(args[1], args[2]);
    case "verdicts":
        return Verdicts(args[1], args[2]);
    case "compare":
        return Compare(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
}

static int Describe(string dir, string manifest)
{
    using MemoryStream ms = new();
    using (Utf8JsonWriter w = new(ms, new JsonWriterOptions { Indented = true }))
    {
        w.WriteStartArray();
        foreach (string path in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            PlaybackDescription d;
            try
            {
                using MediaFile f = Media.Open(path);
                d = f.DescribePlayback();
            }
            catch (Exception e) when (e is MediaException or IOException)
            {
                Console.Error.WriteLine($"{Path.GetFileName(path)}: not opened: {e.Message}");
                continue;
            }

            w.WriteStartObject();
            w.WriteString("file", Path.GetFileName(path));
            w.WriteString("container", d.Container);
            w.WriteString("contentType", d.ContentType);
            w.WriteString("mime", d.MimeType);
            w.WriteBoolean("hasVideo", d.HasVideo);
            w.WriteBoolean("hasAudio", d.Tracks.Any(t => t.Kind == TrackKind.Audio));
            w.WriteStartArray("modes");
            w.WriteStringValue("progressive");
            if (d.IsSegmented && d.MimeType.Contains("codecs=", StringComparison.Ordinal))
            {
                w.WriteStringValue("mediasource");
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    File.WriteAllBytes(manifest, ms.ToArray());
    return 0;
}

static BrowserProfile ProfileOf(string id) => BrowserProfiles.Find(id) ?? throw new ArgumentException("no profile " + id);

static int Verdicts(string dir, string profileId)
{
    BrowserProfile profile = ProfileOf(profileId);
    foreach (string path in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
    {
        PlaybackVerdict v = Media.CanPlay(path, profile);
        string blockers = string.Join(' ', v.Blockers.Select(b => $"{b.Code}[{b.TrackIndex}:{b.Remedy}]"));
        string warnings = string.Join(' ', v.Progressive.Warnings.Concat(v.MediaSource.Warnings).Select(x => x.Code).Distinct());
        Console.WriteLine($"{Path.GetFileName(path)}\tprog={(v.Progressive.Plays ? "Y" : "n")}\tmse={(v.MediaSource.Plays ? "Y" : "n")}\t{v.Description.MimeType}\t{blockers}\t{warnings}");
    }

    return 0;
}

static int Compare(string manifestPath, string resultsPath, string profileId, string? tablePath)
{
    BrowserProfile profile = ProfileOf(profileId);
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
    string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
    Dictionary<string, PlaybackVerdict> ours = [];
    foreach (JsonElement m in manifest.RootElement.EnumerateArray())
    {
        string file = m.GetProperty("file").GetString()!;
        string? path = new[] { Path.Combine(dir, file), Path.Combine(dir, "matrix", file) }.FirstOrDefault(File.Exists);
        if (path is null)
        {
            Console.Error.WriteLine("missing media file " + file);
            return 2;
        }

        ours[file] = Media.CanPlay(path, profile);
    }

    StringBuilder table = new();
    table.AppendLine("file\tbrowser\tmode\tours\tobserved\tresult\tnote");
    int total = 0, bad = 0;
    foreach (string line in File.ReadLines(resultsPath))
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement r = doc.RootElement;
        string file = r.GetProperty("file").GetString()!;
        string mode = r.GetProperty("mode").GetString()!;
        PlaybackVerdict v = ours[file];
        bool ourPlay = mode == "progressive" ? v.Progressive.Plays : v.MediaSource.Plays;
        (bool seen, string note) = Observe(r, mode, v.Description.HasVideo, v.Description.Tracks.Any(t => t.Kind == TrackKind.Audio));
        total++;
        bool same = seen == ourPlay;
        if (!same)
        {
            bad++;
        }

        string why = ourPlay ? "" : string.Join(' ', (mode == "progressive" ? v.Progressive : v.MediaSource).Blockers.Select(b => b.Code));
        table.AppendLine($"{file}\t{r.GetProperty("browser").GetString()}\t{mode}\t{(ourPlay ? "plays" : "blocked " + why)}\t{(seen ? "plays" : "fails")}\t{(same ? "ok" : "DIFF")}\t{note}");
    }

    string text = table.ToString();
    if (tablePath is not null)
    {
        File.WriteAllText(tablePath, text);
    }

    foreach (string l in text.Split('\n').Where(l => l.Contains("\tDIFF\t", StringComparison.Ordinal)))
    {
        Console.WriteLine(l);
    }

    Console.WriteLine($"{profileId}: {total} observations, {bad} disagreements");
    return bad == 0 ? 0 : 1;
}

// A file played when time advanced, nothing errored and, for video, frames were decoded.
static (bool Plays, string Note) Observe(JsonElement r, string mode, bool hasVideo, bool hasAudio)
{
    string Num(string name) => r.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "-";
    string facts = $"cpt={(r.TryGetProperty("canPlayType", out JsonElement c) ? c.GetString() : "-")} ts={(r.TryGetProperty("isTypeSupported", out JsonElement t) ? t.ToString() : "-")} t={Num("currentTime")} frames={Num("totalVideoFrames")}/{Num("webkitDecodedFrameCount")}/{Num("mozDecodedFrames")} w={Num("videoWidth")} peak={Num("audioPeak")} err={(r.TryGetProperty("error", out JsonElement er) ? er.GetProperty("code").ToString() + ":" + er.GetProperty("message").GetString() : (r.TryGetProperty("playError", out JsonElement pe) ? pe.GetString() : "-"))}";
    if (r.TryGetProperty("harnessError", out JsonElement h))
    {
        return (false, "harness: " + h.GetString());
    }

    if (r.TryGetProperty("timedOut", out _))
    {
        return (false, "the page did not answer in 25 s");
    }

    if (mode == "mediasource")
    {
        if (!r.TryGetProperty("isTypeSupported", out JsonElement s) || s.ValueKind != JsonValueKind.True)
        {
            return (false, facts);
        }

        if (r.TryGetProperty("addSourceBufferError", out _) || (r.TryGetProperty("append", out JsonElement a) && a.GetString() != "updateend"))
        {
            return (false, facts + " append=" + (r.TryGetProperty("append", out JsonElement a2) ? a2.GetString() : "-"));
        }
    }

    if (r.TryGetProperty("error", out _) || r.TryGetProperty("playError", out _))
    {
        return (false, facts);
    }

    double time = r.TryGetProperty("currentTime", out JsonElement ct) ? ct.GetDouble() : 0;
    if (time < 0.5)
    {
        return (false, facts);
    }

    if (hasVideo)
    {
        double frames = Math.Max(Math.Max(Count(r, "totalVideoFrames"), Count(r, "webkitDecodedFrameCount")), Count(r, "mozDecodedFrames"));
        double width = r.TryGetProperty("videoWidth", out JsonElement w) ? w.GetDouble() : 0;
        if (frames <= 0 || width <= 0)
        {
            return (false, facts);
        }
    }

    // The generated tone has a steady level well above 0.01; anything quieter means the audio track was dropped or never decoded.
    if (hasAudio && !r.TryGetProperty("audioNotChecked", out _) && Count(r, "audioPeak") < 0.01)
    {
        return (false, facts + " (audio silent)");
    }

    return (true, facts);
}

static double Count(JsonElement r, string name) => r.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
