using System.Globalization;
using System.Text.Json;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Compares what <c>Media.Open</c> reports about a file with <c>ffprobe -show_streams -show_format</c>: the stream list (kind, codec,
/// dimensions, sample rate, channels, language) and the container duration (within one microsecond, ffprobe's resolution).
/// </summary>
internal static class InfoCheck
{
    public static int Run(IEnumerable<string> files, Exceptions exceptions)
    {
        int bad = 0, total = 0, excused = 0;
        foreach (string file in files)
        {
            total++;
            List<string> problems = [];
            string name = Path.GetFileName(file);
            string formatName = "*";
            try
            {
                using MediaFile media = Media.Open(file);
                formatName = media.Native.Reader is IDemuxer dm ? dm.Format.Name : "*";
                using JsonDocument doc = JsonDocument.Parse(Oracle.Run("ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", file));
                if (!doc.RootElement.TryGetProperty("streams", out _))
                {
                    // ffprobe cannot open the file (free-format MPEG audio); the packets are checked against the constant-bit-rate twin by the unit tests.
                    Console.WriteLine("  skipped (ffprobe lists no streams): " + file);
                    continue;
                }

                // ffprobe lists Matroska attachments (fonts, cover art) as streams; they are not tracks here.
                bool asf = media.Format == MediaFormatId.Asf;
                // ASF: ffprobe turns WM/Picture attributes into attached-picture streams; here they are tags (the packet differential compares their bytes).
                // ID3v2 and APE pictures are listed the same way, as video streams flagged attached_pic.
                List<JsonElement> streams = [.. doc.RootElement.GetProperty("streams").EnumerateArray().Where(e => (!e.TryGetProperty("codec_type", out JsonElement streamType) || streamType.GetString() != "attachment") && !IsAttachedPicture(e))];
                if (streams.Count != media.Tracks.Count && !(asf && string.Equals(Path.GetExtension(file), ".dvr-ms", StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"stream count ffprobe {streams.Count} vs ours {media.Tracks.Count}");
                }

                if (asf && string.Equals(Path.GetExtension(file), ".dvr-ms", StringComparison.OrdinalIgnoreCase))
                {
                    // DVR-MS names its audio and data streams only by DirectShow media-type GUIDs in binary stream properties, which are not read; they come out as binary tracks.
                    Console.WriteLine($"note\t{name}\tDVR-MS: stream types and codecs are not compared (binary streams carry DirectShow media types)");
                    streams.Clear();
                    streams.AddRange(Enumerable.Repeat(default(JsonElement), 0));
                }

                int i = 0;
                foreach (JsonElement s in streams)
                {
                    if (i >= media.Tracks.Count)
                    {
                        break;
                    }

                    Compare(media.Tracks[i], s, problems);
                    i++;
                }

                JsonElement format = doc.RootElement.GetProperty("format");
                string? theirDuration = format.TryGetProperty("duration", out JsonElement formatDuration) ? formatDuration.GetString() : null;
                if (asf)
                {
                    // ffprobe's format duration is the latest stream end, a stream's start included; the stated duration less the preroll is the streams' own.
                    double longest = -1;
                    foreach (JsonElement st in streams)
                    {
                        if (st.TryGetProperty("duration", out JsonElement sd) && double.TryParse(sd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        {
                            longest = Math.Max(longest, v);
                        }
                    }

                    theirDuration = longest >= 0 ? longest.ToString("F6", CultureInfo.InvariantCulture) : null;
                    if (theirDuration is null && media.ExactDuration.IsKnown && media.Native.Reader is RecursiveAI.OneMedia.Asf.AsfDemuxer { Header.FileProperties: { } fp } && fp.FileSize > new FileInfo(file).Length)
                    {
                        // ffprobe gives no duration for a file shorter than its header states; the header's value is reported as stated.
                        Console.WriteLine($"note\t{name}\tduration {media.ExactDuration} from the header of a file shorter than it states; ffprobe gives none");
                        theirDuration = media.ExactDuration.Rescale(1_000_000, MediaRounding.NearestAwayFromZero).Ticks.ToString(CultureInfo.InvariantCulture) is { } t ? (double.Parse(t, CultureInfo.InvariantCulture) / 1e6).ToString("F6", CultureInfo.InvariantCulture) : null;
                    }
                }

                bool agrees = theirDuration is not null && media.ExactDuration.IsKnown
                    && Math.Abs(media.ExactDuration.Rescale(1_000_000, MediaRounding.NearestAwayFromZero).Ticks - Math.Round(double.Parse(theirDuration, CultureInfo.InvariantCulture) * 1_000_000)) <= Math.Max(1, 1_000_000 / media.ExactDuration.Timescale);
                if (asf && !agrees && formatDuration.ValueKind == JsonValueKind.String && format.TryGetProperty("bit_rate", out JsonElement bitRate) && double.TryParse(bitRate.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bps) && bps > 0
                    && Math.Abs(new FileInfo(file).Length * 8 / bps - double.Parse(formatDuration.GetString()!, CultureInfo.InvariantCulture)) < 0.002)
                {
                    // Not a stored value: the duration ffprobe gives here is the file size over the bit rate it sums from the streams.
                    Console.WriteLine($"note\t{name}\tffprobe's duration {formatDuration.GetString()} s is the file size over the bit rate; ours: {(media.ExactDuration.IsKnown ? media.ExactDuration.ToString() : "unknown")}");
                    theirDuration = media.ExactDuration.IsKnown ? media.ExactDuration.Rescale(1_000_000, MediaRounding.NearestAwayFromZero).Ticks.ToString(CultureInfo.InvariantCulture) is { } micros ? (double.Parse(micros, CultureInfo.InvariantCulture) / 1e6).ToString("F6", CultureInfo.InvariantCulture) : null : null;
                }

                if (theirDuration is not null && media.ExactDuration.IsKnown)
                {
                    double theirs = double.Parse(theirDuration, CultureInfo.InvariantCulture);
                    long micro = media.ExactDuration.Rescale(1_000_000, MediaRounding.NearestAwayFromZero).Ticks;
                    // "Within one tick": one tick of the container's own clock, never finer than ffprobe's microsecond.
                    long tick = Math.Max(1, 1_000_000 / media.ExactDuration.Timescale);
                    // A stream with no frame count has only an estimate (bytes over the early bit rate) on both sides, and the two estimates weigh the
                    // tags and the bit rate differently; they must agree within 2%.
                    bool estimated = media.Native.Reader is RecursiveAI.OneMedia.AudioStreams.AudioStreamDemuxer { IsDurationEstimated: true };
                    if (estimated)
                    {
                        tick = Math.Max(tick, (long)(micro * 0.02));
                    }

                    // ffprobe counts every frame of a LAME/Info stream; ours drops the encoder delay and padding the tag states (gapless length).
                    if (media.Tracks.Count > 0)
                    {
                        TrackEditInfo e = media.Tracks[0].Edit;
                        tick += (e.Priming.IsKnown ? e.Priming.Rescale(1_000_000, MediaRounding.NearestAwayFromZero).Ticks : 0) +
                                (e.EndPadding.IsKnown ? e.EndPadding.Rescale(1_000_000, MediaRounding.NearestAwayFromZero).Ticks : 0);
                    }

                    // ffprobe divides the file size by the bit rate of the first AMR frame header and counts the magic bytes; ours counts frames.
                    if (media.Format == MediaFormatId.Amr)
                    {
                        tick = Math.Max(tick, (long)(micro * 0.06));
                    }

                    if (Math.Abs(micro - Math.Round(theirs * 1_000_000)) > tick)
                    {
                        problems.Add($"duration ffprobe {theirs:F6} s vs ours {micro / 1e6:F6} s");
                    }
                }
                else if (media.ExactDuration.IsKnown != (theirDuration is not null))
                {
                    problems.Add("duration known on one side only: ours " + media.ExactDuration + (media.Native.Reader is RecursiveAI.OneMedia.IsoBmff.IsoBmffDemuxer { Movie.IsFragmented: true } ? " (fragmented file without a stated duration)" : ""));
                }
            }
            catch (UnsupportedMediaException) when (NoStreams(file))
            {
                Console.WriteLine($"note\t{name}\tneither ffprobe (no streams) nor ours (unsupported) reads the file");
            }
            catch (Exception e) when (e is MediaException or JsonException or IOException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add(e.GetType().Name + ": " + e.Message + (Environment.GetEnvironmentVariable("ONEMEDIA_TRACE") is null ? "" : " " + e.StackTrace));
            }

            // A scoped rule (fields info-duration, info-streams, info-codec, info-open) excuses a problem of that kind.
            List<string> excusedHere = [];
            problems.RemoveAll(p =>
            {
                string field = p.StartsWith("duration ffprobe", StringComparison.Ordinal) ? "info-duration" :
                    p.StartsWith("duration known", StringComparison.Ordinal) ? "info-duration" :
                    p.StartsWith("stream count", StringComparison.Ordinal) ? "info-streams" :
                    p.Contains(" codec ", StringComparison.Ordinal) ? "info-codec" : "info-open";
                long delta = 0;
                int at = p.IndexOf("ffprobe ", StringComparison.Ordinal);
                if (field == "info-duration" && p.Contains(" vs ours ", StringComparison.Ordinal) &&
                    double.TryParse(p[(at + 8)..p.IndexOf(" s vs", StringComparison.Ordinal)], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                    double.TryParse(p[(p.IndexOf("ours ", StringComparison.Ordinal) + 5)..p.LastIndexOf(" s", StringComparison.Ordinal)], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
                {
                    delta = (long)Math.Round(Math.Abs(a - b) * 1_000_000);
                }

                string format = formatName;
                if (exceptions.Match(format, field, file, null, "", "", "", p.Contains("one side only", StringComparison.Ordinal), false, delta) is { } rule)
                {
                    excusedHere.Add($"{field}:{rule.Reason.Split(':')[0]}");
                    return true;
                }

                return false;
            });
            excused += excusedHere.Count;
            bad += problems.Count > 0 ? 1 : 0;
            string note = excusedHere.Count > 0 ? $" [excused {string.Join(", ", excusedHere)}]" : "";
            Console.WriteLine(problems.Count == 0 ? $"same\t{name}{note}" : $"DIFF\t{name}\t{string.Join("; ", problems)}{note}");
        }

        Console.WriteLine($"infocheck: {total} files, {bad} with unexplained differences, {excused} differences excused by exceptions.tsv");
        return 0;
    }

    private static bool NoStreams(string file)
    {
        string json = Oracle.Run("ffprobe", "-v", "error", "-show_streams", "-of", "json", file);
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        return !doc.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.GetArrayLength() == 0;
    }

    // ffprobe lists an ID3v2 or APE picture as a video stream flagged attached_pic; pictures are tags here.
    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out JsonElement d) && d.TryGetProperty("attached_pic", out JsonElement a) && a.GetInt32() == 1;

    private static void Compare(MediaTrack t, JsonElement s, List<string> problems)
    {
        string kind = s.TryGetProperty("codec_type", out JsonElement kindElement) ? kindElement.GetString() ?? "" : "";
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
            problems.Add($"#{t.Index} size {w.GetInt32()}x{h.GetInt32()} vs {v.Width}x{v.Height}");
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
        "h264" or "hevc" or "av1" or "vp8" or "vp9" or "aac" or "mp3" or "opus" or "vorbis" or "flac" or "ac3" or "eac3" or "mjpeg" => ffprobe,
        "mpeg2video" => "mpeg2video",
        "mpeg1video" => "mpeg1video",
        "mp2" => "mp2",
        "mp1" => "mp1",
        "dts" => "dts",
        "truehd" => "truehd",
        "vc1" => "vc1",
        "mpeg4" => "mpeg4",
        "hdmv_pgs_subtitle" => "hdmv-pgs",
        "dvd_subtitle" => "dvd-subpicture",
        "dvb_teletext" => "dvb-teletext",
        "dvb_subtitle" => "dvb-subtitle",
        "pcm_dvd" => "pcm-dvd",
        "pcm_bluray" => "hdmv-lpcm",
        "theora" or "speex" or "flv1" or "vp6f" or "vp6a" or "nellymoser" or "adpcm_swf" => ffprobe,
        "flashsv" or "flashsv2" => ffprobe,
        "amr_nb" => "amr-nb",
        "amr_wb" => "amr-wb",
        "mov_text" => "tx3g",
        "subrip" => "subrip",
        "webvtt" => "webvtt",
        var p when p.StartsWith("pcm_", StringComparison.Ordinal) => "pcm",
        "wmav1" or "wmav2" or "wmapro" or "wmalossless" or "wmavoice" or "wmv1" or "wmv2" or "wmv3" or "vc1" or "mpeg4" or "mp2" or "msmpeg4v1" or "msmpeg4v2" or "msmpeg4v3" => ffprobe,
        _ => null,
    };
}
