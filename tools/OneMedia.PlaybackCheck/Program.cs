using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Playback;
using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Remuxing;

// describe DIR MANIFEST.json            writes the probe manifest for every file in DIR
// verdicts DIR PROFILE                  prints our verdict per file for one profile
// compare MANIFEST RESULTS.jsonl PROFILE [TABLE.tsv]
//                                       compares our verdicts with the browser's observed playback; exit 1 on any disagreement
// makeplayable DIR OUTDIR PROFILE REPORT.tsv
//                                       runs MakePlayable (with audio transcoding) on every file of DIR for one profile and writes what it did
// expect REPORT.tsv MANIFEST RESULTS.jsonl TABLE.tsv
//                                       checks that every file MakePlayable wrote plays (progressive) in the browser, with the audio and video it should have; exit 1 otherwise
// frames FILE                           prints the samples per channel, sample rate and channels the OneAudio decoder yields for the file's audio (edit information applied)
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
    case "makeplayable":
        return MakeAll(args[1], args[2], args[3], args[4]);
    case "expect":
        return Expect(args[1], args[2], args[3], args[4]);
    case "frames":
        return Frames(args[1]);
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
}

static int Frames(string path)
{
    using MediaFile f = Media.Open(path);
    using OneAudio.IAudioDecoder d = f.Decode();
    float[] buffer = new float[4096 * d.Format.Channels];
    long n = 0;
    int got;
    while ((got = d.Read(buffer)) > 0)
    {
        n += got;
    }

    Console.WriteLine($"{n} {d.Format.SampleRate} {d.Format.Channels}");
    return 0;
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

static int MakeAll(string dir, string outDir, string profileId, string reportPath)
{
    BrowserProfile profile = ProfileOf(profileId);
    Directory.CreateDirectory(outDir);
    StringBuilder tsv = new();
    tsv.AppendLine("source\toutput\tstatus\ttarget\tinVideo\tinAudio\tbefore\tremaining\tactions\tseconds");
    foreach (string path in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
    {
        string name = Path.GetFileName(path);
        bool inVideo, inAudio;
        try
        {
            using MediaFile f = Media.Open(path);
            PlaybackDescription d = f.DescribePlayback();
            inVideo = d.HasVideo;
            inAudio = d.Tracks.Any(t => t.Kind == TrackKind.Audio);
        }
        catch (Exception e) when (e is MediaException or IOException)
        {
            tsv.AppendLine($"{name}\t-\tnot-opened\t-\t-\t-\t-\t{e.Message.Replace('\t', ' ')}\t-\t0");
            continue;
        }

        string temp = Path.Combine(outDir, name + ".tmp");
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        MakePlayableReport r;
        try
        {
            r = PlayableMaker.MakePlayable(path, temp, profile, new MakePlayableOptions { AudioTranscoder = AudioTranscoder.Default, Overwrite = true });
        }
        catch (Exception e) when (e is MediaException or IOException or OneAudio.OneAudioException)
        {
            tsv.AppendLine($"{name}\t-\texception\t-\t{inVideo}\t{inAudio}\t-\t{e.GetType().Name}: {e.Message.Replace('\t', ' ')}\t-\t{sw.Elapsed.TotalSeconds:0.0}");
            continue;
        }

        string output = "-";
        if (r.OutputWritten)
        {
            string ext = r.Target switch { RemuxFormat.WebM => ".webm", RemuxFormat.Matroska => ".mkv", RemuxFormat.Mov => ".mov", RemuxFormat.Mp4 => ".mp4", _ => Path.GetExtension(name) };
            output = Path.GetFileNameWithoutExtension(name) + "." + Path.GetExtension(name).TrimStart('.') + ".made" + ext;
            File.Move(temp, Path.Combine(outDir, output), true);
        }
        else if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        string Codes(IEnumerable<PlaybackBlocker> bs) => string.Join(' ', bs.Select(b => $"{b.Code}[{b.TrackIndex}]"));
        string actions = string.Join("; ", r.Actions.Select(a => a.Kind + (a.Track >= 0 ? "#" + a.Track : "")));
        tsv.AppendLine($"{name}\t{output}\t{r.Status}\t{r.Target?.ToString() ?? "-"}\t{inVideo}\t{inAudio}\t{Codes(r.Before.Progressive.Blockers)}\t{Codes(r.Remaining)}\t{actions}\t{sw.Elapsed.TotalSeconds:0.0}");
    }

    File.WriteAllText(reportPath, tsv.ToString());
    string[] lines = tsv.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
    foreach (IGrouping<string, string> g in lines.GroupBy(l => l.Split('\t')[2]).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"{profileId}: {g.Key} {g.Count()}");
    }

    return 0;
}

static int Expect(string reportPath, string manifestPath, string resultsPath, string tablePath)
{
    Dictionary<string, string[]> report = File.ReadLines(reportPath).Skip(1).Select(l => l.Split('\t')).Where(f => f[1] != "-").ToDictionary(f => f[1], f => f, StringComparer.Ordinal);
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
    Dictionary<string, (bool Video, bool Audio)> described = manifest.RootElement.EnumerateArray().ToDictionary(m => m.GetProperty("file").GetString()!, m => (m.GetProperty("hasVideo").GetBoolean(), m.GetProperty("hasAudio").GetBoolean()), StringComparer.Ordinal);
    StringBuilder table = new();
    table.AppendLine("output\tstatus\tresult\tnote");
    int total = 0, bad = 0;
    foreach (string line in File.ReadLines(resultsPath))
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement r = doc.RootElement;
        string file = r.GetProperty("file").GetString()!;
        string mode = r.GetProperty("mode").GetString()!;
        if (mode != "progressive" || !report.TryGetValue(file, out string[]? row))
        {
            continue;
        }

        (bool video, bool audio) = described[file];
        bool wantVideo = bool.Parse(row[4]), wantAudio = bool.Parse(row[5]);
        (bool plays, string note) = Observe(r, mode, video, audio);
        string shape = video == wantVideo && audio == wantAudio ? "" : $" tracks changed: in V={wantVideo} A={wantAudio}, out V={video} A={audio}";
        total++;
        bool ok = plays && shape.Length == 0;
        if (!ok)
        {
            bad++;
        }

        table.AppendLine($"{file}\t{row[2]}\t{(ok ? "ok" : "FAIL")}\t{note}{shape}");
    }

    string text = table.ToString();
    File.WriteAllText(tablePath, text);
    foreach (string l in text.Split('\n').Where(l => l.Contains("\tFAIL\t", StringComparison.Ordinal)))
    {
        Console.WriteLine(l);
    }

    Console.WriteLine($"{total} outputs played in the browser, {bad} failed");
    return bad == 0 ? 0 : 1;
}
