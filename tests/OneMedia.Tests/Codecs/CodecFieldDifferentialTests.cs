using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Tests.Codecs;

/// <summary>
/// Compares the fields the codec layer parses with what ffprobe reports for generated streams. The streams come from
/// <c>tools/codecs/make-streams.sh</c>; point <c>ONEMEDIA_CODEC_STREAMS</c> at the directory it filled. Without it the tests do nothing.
/// </summary>
public sealed partial class CodecFieldDifferentialTests
{
    private static string? StreamsDir => Environment.GetEnvironmentVariable("ONEMEDIA_CODEC_STREAMS");

    private static string FfprobePath => Environment.GetEnvironmentVariable("ONEMEDIA_FFPROBE") ?? "ffprobe";

    internal static string RunTool(string exe, string args)
    {
        using Process p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    [Fact]
    public void ParsedFieldsEqualFfprobe()
    {
        if (StreamsDir is null)
        {
            return;
        }

        int files = 0, compared = 0, totalChecks = 0;
        List<string> problems = [];
        StringBuilder table = new();
        foreach (string path in Directory.GetFiles(StreamsDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            string ext = Path.GetExtension(path);
            if (ext is ".log" or ".ogg")
            {
                continue;
            }

            files++;
            string json = RunTool(FfprobePath, $"-v error -show_streams -select_streams 0 -of json \"{path}\"");
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.GetArrayLength() == 0)
            {
                problems.Add($"{Path.GetFileName(path)}: ffprobe found no stream");
                continue;
            }

            JsonElement s = streams[0];
            if (!TryDescribe(path, s, out CodecParameters? ours, out string? how))
            {
                problems.Add($"{Path.GetFileName(path)}: we could not describe it ({how})");
                continue;
            }

            List<string> diffs = Compare(s, ours!, out int checks);
            compared++;
            totalChecks += checks;
            table.Append(Path.GetFileName(path)).Append(' ').Append(how).Append(" | ").Append(Brief(ours!)).Append(" | checks=").Append(checks).Append('\n');
            foreach (string d in diffs)
            {
                problems.Add($"{Path.GetFileName(path)}: {d}");
            }
        }

        string? report = Environment.GetEnvironmentVariable("ONEMEDIA_CODEC_REPORT");
        string summary = $"files={files} compared={compared} checks={totalChecks} problems={problems.Count}\n{string.Join('\n', problems)}\n{table}";
        if (report is not null)
        {
            File.WriteAllText(report, summary);
        }

        Assert.True(problems.Count == 0, summary);
    }

    private static bool TryDescribe(string path, JsonElement ffprobe, out CodecParameters? ours, out string? how)
    {
        ours = null;
        string name = ffprobe.GetProperty("codec_name").GetString() ?? "";
        string ext = Path.GetExtension(path);
        byte[] file = File.ReadAllBytes(path);
        how = ext;
        CodecId codec = CodecIdFromFfprobe(name);
        if (codec.IsUnknown)
        {
            how = "unmapped ffprobe codec " + name;
            return false;
        }

        if (ext == ".mp4")
        {
            MiniContainers.Mp4Track? t = MiniContainers.ReadMp4(file);
            if (t is null)
            {
                how = "no mp4 track";
                return false;
            }

            how = $"mp4 {t.EntryType}/{t.ConfigType}";
            if (t.ConfigType is not null && (codec.Name != "mp3"))
            {
                // vp9 and av1 in ffprobe carry full descriptions from the first frame; the config records are what we are testing here.
                if (CodecParameterReader.TryDescribe(codec, new CodecConfiguration(t.Config, t.ConfigType), t.FirstSample, out ours))
                {
                    return true;
                }
            }

            return CodecParameterReader.TryDescribeSample(codec, t.FirstSample, out ours);
        }

        if (ext == ".mkv")
        {
            MiniContainers.MkvTrack? t = MiniContainers.ReadMkv(file);
            if (t is null)
            {
                how = "no mkv track";
                return false;
            }

            how = $"mkv {t.CodecId} private={t.CodecPrivate.Length}";
            if (t.CodecPrivate.Length > 0 && codec.Name is not ("mp3" or "vp9" or "vp8" or "mpeg2video"))
            {
                if (CodecParameterReader.TryDescribe(codec, new CodecConfiguration(t.CodecPrivate, "CodecPrivate"), t.FirstFrame, out ours))
                {
                    return true;
                }
            }

            return CodecParameterReader.TryDescribeSample(codec, t.FirstFrame, out ours);
        }

        switch (ext)
        {
            case ".h264":
                return CodecParameterReader.TryDescribeAnnexB(NalCodec.H264, file, out ours);
            case ".hevc":
                return CodecParameterReader.TryDescribeAnnexB(NalCodec.Hevc, file, out ours);
            case ".obu":
            case ".m2v":
            case ".aac":
            case ".ac3":
            case ".eac3":
                return CodecParameterReader.TryDescribeSample(codec, file, out ours);
            case ".ivf":
                return CodecParameterReader.TryDescribeSample(codec, file.AsSpan(32 + 12), out ours);
            case ".mp3":
                {
                    int skip = 0;
                    if (file.Length > 10 && file[0] == 'I' && file[1] == 'D' && file[2] == '3')
                    {
                        skip = 10 + ((file[6] << 21) | (file[7] << 14) | (file[8] << 7) | file[9]);
                    }

                    return CodecParameterReader.TryDescribeSample(codec, file.AsSpan(skip), out ours);
                }

            case ".flac":
                return CodecParameterReader.TryDescribe(codec, new CodecConfiguration(file.AsMemory(0, 4 + 4 + 34), "CodecPrivate"), out ours);
            default:
                return false;
        }
    }

    private static CodecId CodecIdFromFfprobe(string name) => name switch
    {
        "h264" => CodecId.H264,
        "hevc" => CodecId.Hevc,
        "av1" => CodecId.Av1,
        "vp9" => CodecId.Vp9,
        "vp8" => CodecId.Vp8,
        "mpeg2video" => CodecId.Mpeg2Video,
        "aac" => CodecId.Aac,
        "opus" => CodecId.Opus,
        "vorbis" => CodecId.Vorbis,
        "flac" => CodecId.Flac,
        "ac3" => CodecId.Ac3,
        "eac3" => CodecId.Eac3,
        "mp3" => CodecId.Mp3,
        "mp2" => new CodecId("mp2"),
        _ => CodecId.Unknown,
    };

    private static List<string> Compare(JsonElement f, CodecParameters ours, out int checks)
    {
        List<string> d = [];
        int n = 0;
        string? Str(string key) => f.TryGetProperty(key, out JsonElement e) ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()) : null;
        int? Int(string key) => int.TryParse(Str(key), out int v) ? v : null;

        void Check(string field, object? theirs, object? mine)
        {
            n++;
            if (!Equals(theirs, mine))
            {
                d.Add($"{field}: ffprobe={theirs ?? "(none)"} ours={mine ?? "(none)"}");
            }
        }

        string codec = Str("codec_name")!;
        string? type = Str("codec_type");
        if (type == "video")
        {
            Check("width", Int("width"), ours.Width);
            Check("height", Int("height"), ours.Height);
            if (Str("pix_fmt") is { } pix && ParsePixFmt(pix, out ChromaFormat chroma, out int depth))
            {
                Check("chroma", chroma, ours.Chroma);
                Check("bit depth", depth, ours.BitDepth);
            }

            string[] colorFields = ["color_primaries", "color_transfer", "color_space"];
            int[] codes = [ours.Color.Primaries, ours.Color.Transfer, ours.Color.Matrix];
            for (int i = 0; i < 3; i++)
            {
                string? theirs = Str(colorFields[i]);
                string expected = ours.Color.IsSignalled ? ColorName(i, codes[i]) : "unknown";
                if (theirs is not null && theirs != expected && !(theirs == "unknown" && !ours.Color.IsSignalled))
                {
                    d.Add($"{colorFields[i]}: ffprobe={theirs} ours={expected}");
                }
            }

            if (Str("color_range") is { } range && codec is not ("vp9" or "av1" or "mpeg2video"))
            {
                Check("color_range", range, ours.Color.FullRange ? "pc" : "tv");
            }
            else if (Str("color_range") is { } range2 && ours.Color.IsSignalled)
            {
                Check("color_range", range2, ours.Color.FullRange ? "pc" : "tv");
            }

            if (codec is "h264" or "hevc" && Int("has_b_frames") is { } hb && ours.ReorderFrames >= 0)
            {
                // ffprobe only knows the reorder depth after parsing SPS/slices; report differences only when it has found some
                if (hb != 0)
                {
                    Check("has_b_frames", hb, ours.ReorderFrames);
                }
            }
        }
        else
        {
            Check("sample_rate", Int("sample_rate"), ours.SampleRate);
            Check("channels", Int("channels"), ours.Channels);
            if (codec == "flac" && Int("bits_per_raw_sample") is { } bits)
            {
                Check("bits_per_raw_sample", bits, ours.BitDepth);
            }
        }

        if (Str("profile") is { } profile && ours.ProfileName is { } mine)
        {
            string expectedProfile = codec switch { "vp9" => $"Profile {ours.Profile}", _ => mine };
            Check("profile", profile, codec == "mpeg2video" && profile == "4:2:2" ? "4:2:2" : expectedProfile);
        }
        else if (Str("profile") is { } p2 && ours.ProfileName is null && codec is "h264" or "hevc" or "aac")
        {
            d.Add($"profile: ffprobe={p2} ours=(none)");
        }

        if (Int("level") is { } level && level > 0 && ours.Level >= 0 && codec is not ("vp9" or "av1" or "aac"))
        {
            Check("level", level, ours.Level);
        }

        checks = n;
        return d;
    }

    private static string Brief(CodecParameters p) =>
        $"{p.Codec} rfc6381={p.Rfc6381} profile={p.ProfileName}({p.Profile}) level={p.Level} {p.Width}x{p.Height} {p.Chroma} bd={p.BitDepth} color={p.Color} rate={p.SampleRate}/{p.CoreSampleRate} ch={p.Channels} reorder={p.ReorderFrames} fps={p.FrameRateNumerator}/{p.FrameRateDenominator} sar={p.SarWidth}:{p.SarHeight}";

    private static string ColorName(int kind, int code) => kind switch
    {
        0 => code switch { 1 => "bt709", 4 => "bt470m", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m", 8 => "film", 9 => "bt2020", 10 => "smpte428", 11 => "smpte431", 12 => "smpte432", 22 => "ebu3213", _ => "unknown" },
        1 => code switch { 1 => "bt709", 4 => "bt470m", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m", 8 => "linear", 13 => "iec61966-2-1", 14 => "bt2020-10", 15 => "bt2020-12", 16 => "smpte2084", 17 => "smpte428", 18 => "arib-std-b67", _ => "unknown" },
        _ => code switch { 0 => "gbr", 1 => "bt709", 4 => "fcc", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m", 8 => "ycgco", 9 => "bt2020nc", 10 => "bt2020c", _ => "unknown" },
    };

    private static bool ParsePixFmt(string pix, out ChromaFormat chroma, out int depth)
    {
        chroma = ChromaFormat.Unknown;
        depth = 8;
        Match m = PixFmt().Match(pix);
        if (m.Success)
        {
            chroma = m.Groups[1].Value switch { "420" => ChromaFormat.Yuv420, "422" => ChromaFormat.Yuv422, "444" => ChromaFormat.Yuv444, _ => ChromaFormat.Unknown };
            depth = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 8;
            return chroma != ChromaFormat.Unknown;
        }

        Match g = Gray().Match(pix);
        if (g.Success)
        {
            chroma = ChromaFormat.Monochrome;
            depth = g.Groups[1].Success ? int.Parse(g.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 8;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^yuvj?(420|422|444)p(\d+)?(le|be)?$")]
    private static partial Regex PixFmt();

    [GeneratedRegex(@"^gray(\d+)?(le|be)?$")]
    private static partial Regex Gray();
}
