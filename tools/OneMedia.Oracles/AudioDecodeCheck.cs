using System.Globalization;
using System.Text;
using OneAudio;
using RecursiveAI.OneMedia.Audio;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Decodes the first audio stream of each file two ways and compares the samples: through the OneMedia adapter, and through OneAudio's
/// own path for the same stream (OneAudio's MP4 reader for MP4-family files; for Matroska, the stream remuxed with <c>ffmpeg -c copy</c>
/// to Ogg, MP4, FLAC or MP3 and read by OneAudio). Also reports the frame count against ffprobe's duration times rate.
/// </summary>
internal static class AudioDecodeCheck
{
    private const int ChunkFrames = 4096;

    public static int Run(IEnumerable<string> files, string work)
    {
        Directory.CreateDirectory(work);
        Console.WriteLine("file\tcodec\tchannels\trate\tourFrames\tnativeFrames\tfirstDiff\tmaxAbsDiff\tffprobeFrames\tverdict\tnote");
        int bad = 0;
        foreach (string file in files)
        {
            string line;
            try
            {
                line = One(file, work);
            }
            catch (Exception e) when (e is OneAudioException or MediaException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                line = $"{Path.GetFileName(file)}\t\t\t\t\t\t\t\t\tskipped\terror {e.GetType().Name}: {e.Message.Replace('\t', ' ').Replace('\n', ' ')} [{string.Join(" < ", (e.StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim().Split('(')[0]))}]";
            }

            if (!line.Contains("\tsame\t", StringComparison.Ordinal))
            {
                bad++;
            }

            Console.WriteLine(line);
        }

        Console.Error.WriteLine($"audiodecode: {bad} of the files did not match exactly");
        return 0;
    }

    private static string One(string file, string work)
    {
        string name = Path.GetFileName(file);
        string ext = Path.GetExtension(file).ToLowerInvariant();
        bool iso = ext is ".mp4" or ".m4a" or ".3gp" or ".3g2" or ".mov";
        (string codec, int rate, int channels, long probeFrames) = Probe(file);
        string nativePath = file;
        AudioCodecRegistry nativeRegistry = new AudioCodecRegistry().AddMp4().AddAac().AddAmrNb().AddAmrWb();
        if (!iso)
        {
            string target = codec switch { "opus" or "vorbis" => ".ogg", "flac" => ".flac", "aac" => ".m4a", "mp3" => ".mp3", _ => ".mka" };
            nativePath = Path.Combine(work, Path.GetFileNameWithoutExtension(file) + "-native" + target);
            using (System.Diagnostics.Process p = Oracle.Start("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-i", file, "-map", "0:a:0", "-c", "copy", nativePath], redirectStdErr: true))
            {
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    return $"{name}\t{codec}\t{channels}\t{rate}\t\t\t\t\t{probeFrames}\tskipped\tffmpeg could not remux the stream";
                }
            }

            nativeRegistry = new AudioCodecRegistry().AddOgg().AddVorbis().AddOpus().AddFlac().AddMp4().AddAac().AddMp3();
        }

        AudioCodecRegistry ours = MediaAudioExtensions.CreateRegistry();
        IAudioDecoder a;
        try
        {
            a = AudioDecoder.OpenFile(file, ours);
        }
        catch (UnsupportedAudioFormatException e)
        {
            return $"{name}\t{codec}\t{channels}\t{rate}\t\t\t\t\t{probeFrames}\tskipped\tours unsupported: {e.Message}";
        }

        using (a)
        {
            IAudioDecoder b;
            try
            {
                b = AudioDecoder.OpenFile(nativePath, nativeRegistry);
            }
            catch (UnsupportedAudioFormatException e)
            {
                return $"{name}\t{codec}\t{channels}\t{rate}\t{CountFrames(a)}\t\t\t\t{probeFrames}\tskipped\tnative unsupported: {e.Message}";
            }

            using (b)
            {
                string line = Compare(name, codec, a, b, probeFrames);
                if (line.Contains("\tdiffers\t", StringComparison.Ordinal) && !line.Contains("format differs", StringComparison.Ordinal))
                {
                    int shift = FindShift(file, ours, nativePath, nativeRegistry, out bool nativeAhead);
                    if (shift != 0)
                    {
                        line = line.Replace("\tdiffers\t", "\tshifted-equal\t", StringComparison.Ordinal) + $"; first 20000 frames equal when {(nativeAhead ? "the native stream is" : "our stream is")} offset by {shift} frames";
                    }
                }

                return line;
            }
        }
    }

    // Tries the usual codec-delay offsets: if one side's first 20000 frames equal the other's after skipping that many, the two differ in priming only.
    private static int FindShift(string ourPath, AudioCodecRegistry ours, string nativePath, AudioCodecRegistry native, out bool nativeAhead)
    {
        nativeAhead = true;
        foreach (int shift in new[] { 1024, 2048, 1105, 1681, 529, 576, 1152, 2257, 312, 624, 1000 })
        {
            foreach (bool skipNative in new[] { true, false })
            {
                using IAudioDecoder x = AudioDecoder.OpenFile(ourPath, ours);
                using IAudioDecoder y = AudioDecoder.OpenFile(nativePath, native);
                if (x.Format.Channels != y.Format.Channels)
                {
                    return 0;
                }

                float[] skip = new float[shift * x.Format.Channels];
                if (Fill(skipNative ? y : x, skip) < shift)
                {
                    continue;
                }

                float[] p = new float[20000 * x.Format.Channels], q = new float[p.Length];
                int n = Math.Min(Fill(x, p), Fill(y, q));
                if (n > 4000 && p.AsSpan(0, n * x.Format.Channels).SequenceEqual(q.AsSpan(0, n * x.Format.Channels)))
                {
                    nativeAhead = skipNative;
                    return shift;
                }
            }
        }

        return 0;
    }

    private static long CountFrames(IAudioDecoder d)
    {
        float[] buf = new float[ChunkFrames * Math.Max(1, d.Format.Channels)];
        long n = 0;
        int got;
        while ((got = d.Read(buf)) > 0)
        {
            n += got;
        }

        return n;
    }

    private static string Compare(string name, string codec, IAudioDecoder a, IAudioDecoder b, long probeFrames)
    {
        int ch = a.Format.Channels;
        string note = a.Format.SampleRate == b.Format.SampleRate && ch == b.Format.Channels ? "" : $"format differs: ours {a.Format.SampleRate}/{ch}, native {b.Format.SampleRate}/{b.Format.Channels}";
        float[] x = new float[ChunkFrames * ch], y = new float[ChunkFrames * Math.Max(1, b.Format.Channels)];
        long na = 0, nb = 0, first = -1;
        double max = 0;
        while (true)
        {
            int ga = Fill(a, x), gb = Fill(b, y);
            int common = Math.Min(ga, gb);
            if (first < 0 && ch == b.Format.Channels)
            {
                for (int i = 0; i < common * ch; i++)
                {
                    if (BitConverter.SingleToInt32Bits(x[i]) != BitConverter.SingleToInt32Bits(y[i]))
                    {
                        first = na * ch + i;
                        break;
                    }
                }
            }

            if (ch == b.Format.Channels)
            {
                for (int i = 0; i < common * ch; i++)
                {
                    max = Math.Max(max, Math.Abs((double)x[i] - y[i]));
                }
            }

            na += ga;
            nb += gb;
            if (ga == 0 && gb == 0)
            {
                break;
            }
        }

        if (na != nb)
        {
            note += (note.Length > 0 ? "; " : "") + $"length differs by {na - nb} frames";
        }

        string verdict = first >= 0 || note.StartsWith("format", StringComparison.Ordinal) ? "differs" : na == nb ? "same" : "prefix-equal";
        return string.Create(CultureInfo.InvariantCulture, $"{name}\t{codec}\t{ch}\t{a.Format.SampleRate}\t{na}\t{nb}\t{(first < 0 ? "-" : first.ToString(CultureInfo.InvariantCulture))}\t{max:G6}\t{probeFrames}\t{verdict}\t{note}");
    }

    private static int Fill(IAudioDecoder d, float[] buf)
    {
        int ch = d.Format.Channels;
        int frames = 0;
        while (frames * ch < buf.Length)
        {
            int got = d.Read(buf.AsSpan(frames * ch));
            if (got <= 0)
            {
                break;
            }

            frames += got;
        }

        return frames;
    }

    // First audio stream: codec, rate, channels and the frame count ffprobe's duration implies (rounded).
    private static (string Codec, int Rate, int Channels, long Frames) Probe(string file)
    {
        string text = Oracle.Run("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,duration", "-of", "csv=p=0", file).Trim();
        string[] f = text.Split(',');
        string codec = f.Length > 0 ? f[0] : "";
        int rate = f.Length > 1 && int.TryParse(f[1], CultureInfo.InvariantCulture, out int r) ? r : 0;
        int ch = f.Length > 2 && int.TryParse(f[2], CultureInfo.InvariantCulture, out int c) ? c : 0;
        long frames = f.Length > 3 && double.TryParse(f[3], CultureInfo.InvariantCulture, out double d) ? (long)Math.Round(d * rate) : -1;
        if (frames < 0 && rate > 0 && double.TryParse(Oracle.Run("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file).Trim(), CultureInfo.InvariantCulture, out double fd))
        {
            frames = (long)Math.Round(fd * rate);
        }

        codec = codec switch { "amr_nb" => "amr-nb", "amr_wb" => "amr-wb", _ => codec };
        return (codec, rate, ch, frames);
    }
}
