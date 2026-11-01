using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>Where the pinned reference tools live and how they are run.</summary>
internal static class Oracle
{
    /// <summary>The oracle directory: <c>$ONEMEDIA_HOME/oracles/bin</c>, falling back to the PATH.</summary>
    public static string Tool(string name)
    {
        string home = Environment.GetEnvironmentVariable("ONEMEDIA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemedia");
        string candidate = Path.Combine(home, "oracles", "bin", name);
        return File.Exists(candidate) ? candidate : name;
    }

    /// <summary>Corpus root: <c>$ONEMEDIA_HOME/corpus</c>.</summary>
    public static string CorpusRoot
    {
        get
        {
            string home = Environment.GetEnvironmentVariable("ONEMEDIA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemedia");
            return Path.Combine(home, "corpus");
        }
    }

    /// <summary>Starts a tool and returns its process with stdout redirected.</summary>
    public static Process Start(string tool, IEnumerable<string> args, bool redirectStdErr = true)
    {
        ProcessStartInfo psi = new(Tool(tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = redirectStdErr,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + tool);
    }

    /// <summary>Runs a tool to completion and returns its standard output.</summary>
    public static string Run(string tool, params string[] args)
    {
        using Process p = Start(tool, args);
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        err.Wait();
        return output;
    }
}

/// <summary>One packet in the normalised form both sides are reduced to.</summary>
internal readonly record struct PacketRecord(int Track, MediaTime Pts, MediaTime Dts, MediaTime Duration, long Size, long Pos, bool Key, string Hash);

/// <summary>A stream as ffprobe reports it, with the exact time base its packet times are in.</summary>
internal sealed record ProbeStream(int Index, long Id, string CodecType, string CodecName, long TimeBaseNum, long TimeBaseDen, bool AttachedPicture = false)
{
    /// <summary>A time in this stream's time base, exact whatever the base's numerator.</summary>
    public MediaTime Time(long ticks) => new(checked(ticks * TimeBaseNum), TimeBaseDen);
}

/// <summary>Reads ffprobe's view of a file: streams, then every packet with its payload hash.</summary>
internal static class FfprobeReader
{
    /// <summary>The streams, from <c>-show_streams -of json</c>.</summary>
    public static List<ProbeStream> ReadStreams(string path)
    {
        string json = Oracle.Run("ffprobe", "-v", "error", "-show_streams", "-of", "json", path);
        List<ProbeStream> result = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // ffprobe gave up part-way through its output (a header it cannot make sense of): it found no streams.
            return result;
        }

        using JsonDocument parsed = doc;
        if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams))
        {
            return result;
        }

        foreach (JsonElement s in streams.EnumerateArray())
        {
            string tb = s.TryGetProperty("time_base", out JsonElement t) ? t.GetString() ?? "1/1" : "1/1";
            int slash = tb.IndexOf('/', StringComparison.Ordinal);
            long num = long.Parse(tb[..slash], CultureInfo.InvariantCulture);
            long den = long.Parse(tb[(slash + 1)..], CultureInfo.InvariantCulture);
            long id = 0;
            if (s.TryGetProperty("id", out JsonElement idEl) && idEl.GetString() is { } idText)
            {
                id = idText.StartsWith("0x", StringComparison.Ordinal)
                    ? long.Parse(idText.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : long.Parse(idText, CultureInfo.InvariantCulture);
            }

            result.Add(new ProbeStream(
                s.GetProperty("index").GetInt32(),
                id,
                s.TryGetProperty("codec_type", out JsonElement ct) ? ct.GetString() ?? "" : "",
                s.TryGetProperty("codec_name", out JsonElement cn) ? cn.GetString() ?? "" : "",
                num,
                den,
                s.TryGetProperty("disposition", out JsonElement disp) && disp.TryGetProperty("attached_pic", out JsonElement ap) && ap.GetInt32() == 1));
        }

        return result;
    }

    /// <summary>
    /// Packets in file order. ffprobe's compact writer is used instead of one big JSON document so a
    /// multi-gigabyte file streams through in constant memory. Times are in each stream's time base.
    /// </summary>
    public static IEnumerable<PacketRecord> ReadPackets(string path, IReadOnlyList<ProbeStream> streams)
    {
        using Process p = Oracle.Start("ffprobe", ["-v", "error", "-show_packets", "-show_data_hash", "SHA256", "-of", "compact", path]);
        Task<string> err = p.StandardError.ReadToEndAsync();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            if (!line.StartsWith("packet|", StringComparison.Ordinal))
            {
                continue;
            }

            yield return ParsePacket(line, streams);
        }

        p.WaitForExit();
        err.Wait();
    }

    internal static PacketRecord ParsePacket(string line, IReadOnlyList<ProbeStream> streams)
    {
        int track = 0;
        string? pts = null, dts = null, duration = null, flags = null, hash = null, pos = null;
        long size = 0;
        foreach (string field in line.Split('|'))
        {
            int eq = field.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            string value = field[(eq + 1)..];
            switch (field[..eq])
            {
                case "stream_index": track = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "pts": pts = value; break;
                case "dts": dts = value; break;
                case "duration": duration = value; break;
                case "size": size = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "pos": pos = value; break;
                case "flags": flags = value; break;
                case "data_hash": hash = value; break;
            }
        }

        ProbeStream s = streams.First(x => x.Index == track);
        MediaTime T(string? v) => v is null || v == "N/A" ? MediaTime.Unknown : s.Time(long.Parse(v, CultureInfo.InvariantCulture));
        string h = hash is null ? "" : hash[(hash.IndexOf(':', StringComparison.Ordinal) + 1)..].ToLowerInvariant();
        return new PacketRecord(track, T(pts), T(dts), T(duration), size, pos is null || pos == "N/A" ? -1 : long.Parse(pos, CultureInfo.InvariantCulture), flags is not null && flags.Contains('K', StringComparison.Ordinal), h);
    }
}
