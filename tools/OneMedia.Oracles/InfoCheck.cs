using System.Globalization;
using System.Text.Json;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Compares what <c>Media.Open</c> reports about a file with <c>ffprobe -show_streams -show_format</c>: the stream list (kind, codec,
/// dimensions, sample rate, channels, language) and the container duration (within one microsecond, ffprobe's resolution).
/// </summary>
internal static class InfoCheck
{
    public static int Run(IEnumerable<string> files)
    {
        int bad = 0, total = 0;
        foreach (string file in files)
        {
            total++;
            List<string> problems = [];
            List<string> explained = [];
            string name = Path.GetFileName(file);
            try
            {
                using MediaFile media = Media.Open(file);
                using JsonDocument doc = JsonDocument.Parse(Oracle.Run("ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", file));
                // ffprobe lists Matroska attachments (fonts, cover art) as streams; they are not tracks here.
                List<JsonElement> streams = [.. doc.RootElement.GetProperty("streams").EnumerateArray().Where(e => e.GetProperty("codec_type").GetString() != "attachment")];
                if (streams.Count != media.Tracks.Count)
                {
                    // ffprobe synthesizes the audio streams of DV frames (the sound is shuffled across the frame and needs a decoder step to extract).
                    bool dvAudio = media.Tracks.Any(t => t.Codec.Name == "dvvideo") && streams.Count > media.Tracks.Count
                        && streams.Skip(media.Tracks.Count).All(e => e.TryGetProperty("codec_name", out JsonElement c) && c.GetString() == "pcm_s16le");
                    (dvAudio ? explained : problems).Add($"stream count ffprobe {streams.Count} vs ours {media.Tracks.Count}" + (dvAudio ? " (dv-embedded-audio)" : ""));
                }

                int i = 0;
                foreach (JsonElement s in streams)
                {
                    if (i >= media.Tracks.Count)
                    {
                        break;
                    }

                    Compare(media.Tracks[i], s, problems, explained);
                    i++;
                }

                if (doc.RootElement.GetProperty("format").TryGetProperty("duration", out JsonElement d) && media.ExactDuration.IsKnown)
                {
                    double theirs = double.Parse(d.GetString()!, CultureInfo.InvariantCulture);
                    long micro = media.ExactDuration.Rescale(1_000_000, MediaRounding.NearestAwayFromZero).Ticks;
                    // "Within one tick": one tick of the container's own clock, never finer than ffprobe's microsecond.
                    long tick = Math.Max(1, 1_000_000 / media.ExactDuration.Timescale);
                    if (Math.Abs(micro - Math.Round(theirs * 1_000_000)) > tick)
                    {
                        string text = $"duration ffprobe {theirs:F6} s vs ours {micro / 1e6:F6} s";
                        bool audioWithoutDuration = streams.Any(e => e.GetProperty("codec_type").GetString() == "audio" && !e.TryGetProperty("duration", out _));
                        if (media.Defects.Any(x => x.Code == "riff.size-past-end") && theirs < micro / 1e6)
                        {
                            // A file cut short: ffprobe estimates the length from the data present, ours is the length the headers state.
                            explained.Add(text + " (truncated-estimate)");
                        }
                        else if (audioWithoutDuration && Math.Abs((micro / 1e6) - theirs) < 0.5)
                        {
                            // ffprobe gives MPEG audio and AC-3 streams in AVI no duration and takes the file's from the video; ours is the longest stream header.
                            explained.Add(text + " (audio-stream-duration)");
                        }
                        else
                        {
                            problems.Add(text);
                        }
                    }
                }
                else if (media.ExactDuration.IsKnown != doc.RootElement.GetProperty("format").TryGetProperty("duration", out _))
                {
                    problems.Add("duration known on one side only: ours " + media.ExactDuration + (media.Native.Reader is RecursiveAI.OneMedia.IsoBmff.IsoBmffDemuxer { Movie.IsFragmented: true } ? " (fragmented file without a stated duration)" : ""));
                }
            }
            catch (Exception e) when (e is MediaException or JsonException or IOException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add(e.GetType().Name + ": " + e.Message);
            }

            bad += problems.Count > 0 ? 1 : 0;
            Console.WriteLine(problems.Count > 0 ? $"DIFF\t{name}\t{string.Join("; ", problems.Concat(explained))}" : explained.Count > 0 ? $"explained\t{name}\t{string.Join("; ", explained)}" : $"same\t{name}");
        }

        Console.WriteLine($"infocheck: {total} files, {bad} with unexplained differences");
        return 0;
    }

    private static void Compare(MediaTrack t, JsonElement s, List<string> problems, List<string> explained)
    {
        string kind = s.GetProperty("codec_type").GetString() ?? "";
        string expectKind = t.Kind switch { TrackKind.Video => "video", TrackKind.Audio => "audio", TrackKind.Subtitle => "subtitle", TrackKind.Data => "data", _ => "" };
        if (expectKind.Length > 0 && kind != expectKind && !(kind == "attachment" || (kind == "data" && t.Kind == TrackKind.Unknown) || (kind == "data" && t.Kind == TrackKind.Subtitle && t.Codec == CodecId.WebVtt))) // ffprobe does not know WebVTT in MP4 and calls it data
        {
            problems.Add($"#{t.Index} kind {kind} vs {expectKind}");
        }

        string codec = s.TryGetProperty("codec_name", out JsonElement cn) ? cn.GetString() ?? "" : "";
        if (Mapped(codec) is { } expected && !t.Codec.IsUnknown && expected != t.Codec.Name)
        {
            problems.Add($"#{t.Index} codec {codec} vs {t.Codec.Name}");
        }
        else if (Mapped(codec) is not null && t.Codec.IsUnknown)
        {
            problems.Add($"#{t.Index} codec {codec} vs unknown ({t.CodecTag})");
        }

        if (t.Video is { } v && s.TryGetProperty("width", out JsonElement w) && s.TryGetProperty("height", out JsonElement h) && (w.GetInt32() != v.Width || h.GetInt32() != v.Height))
        {
            // ffprobe reads the frame size of motion JPEG from the first frame; the container header may state another height (observed in AVI).
            (codec == "mjpeg" && t.Codec == CodecId.Mjpeg && w.GetInt32() == v.Width && Math.Abs(h.GetInt32() - v.Height) <= 16 ? explained : problems)
                .Add($"#{t.Index} size {w.GetInt32()}x{h.GetInt32()} vs {v.Width}x{v.Height}" + (codec == "mjpeg" ? " (mjpeg-frame-size)" : ""));
        }

        if (t.Audio is { } a)
        {
            if (s.TryGetProperty("sample_rate", out JsonElement r) && int.Parse(r.GetString()!, CultureInfo.InvariantCulture) is var theirRate && theirRate != a.SampleRate
                && !(t.Codec == CodecId.Aac && a.SampleRate == 2 * theirRate)) // Matroska OutputSamplingFrequency (the SBR rate) against the core rate ffprobe reads from the AAC setup bytes
            {
                problems.Add($"#{t.Index} rate {r.GetString()} vs {a.SampleRate}");
            }

            if (s.TryGetProperty("channels", out JsonElement c) && c.GetInt32() != a.Channels)
            {
                problems.Add($"#{t.Index} channels {c.GetInt32()} vs {a.Channels}");
            }
        }

        if (s.TryGetProperty("tags", out JsonElement tags) && tags.TryGetProperty("language", out JsonElement lang) && t.Language is not null && lang.GetString() != t.Language
            && !(t.Language.Length < 3 || t.Language.Contains('-', StringComparison.Ordinal))) // a BCP 47 tag from LanguageBCP47 is ours; ffprobe prints the Language element beside it
        {
            problems.Add($"#{t.Index} language {lang.GetString()} vs {t.Language}");
        }
    }

    // ffprobe codec names that have a neutral id; null for everything else (not compared).
    private static string? Mapped(string ffprobe) => ffprobe switch
    {
        "mpeg4" or "msmpeg4v1" or "msmpeg4v2" or "msmpeg4v3" or "dvvideo" or "huffyuv" or "ffvhuff" or "ffv1" or "mp2" or "mpeg1video" or "wmv1" or "wmv2" or "wmv3" or "cinepak" or "theora" or "svq1" or "svq3" or "msvideo1" or "msrle" or "tscc" => ffprobe,
        var a when a.StartsWith("adpcm_", StringComparison.Ordinal) => ffprobe,
        "h264" or "hevc" or "av1" or "vp8" or "vp9" or "aac" or "mp3" or "opus" or "vorbis" or "flac" or "ac3" or "eac3" or "mjpeg" => ffprobe,
        "mpeg2video" => "mpeg2video",
        "amr_nb" => "amr-nb",
        "amr_wb" => "amr-wb",
        "mov_text" => "tx3g",
        "subrip" => "subrip",
        "webvtt" => "webvtt",
        var p when p.StartsWith("pcm_", StringComparison.Ordinal) => "pcm",
        _ => null,
    };
}
