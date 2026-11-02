using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMedia.Streaming.Sources;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>Checks for presentation sources and growing files: packet differentials against ffprobe, I/O over the range server, and live reading.</summary>
internal static class StreamingChecks
{
    /// <summary>
    /// <c>presdiff [OURS[=THEIRS]]...</c>: compares our packets for each playlist or manifest with ffprobe's. When the playlist ours opens is not
    /// the one ffprobe should open (a multivariant playlist, where ffprobe lists every variant), give ffprobe's after an equals sign.
    /// </summary>
    public static int PresentationDiff(List<string> args, string outDir, Exceptions exceptions)
    {
        MediaFormatRegistry registry = Readers.Create();
        int bad = 0;
        foreach (string arg in args)
        {
            int eq = arg.IndexOf('=', StringComparison.Ordinal);
            string ours = Path.GetFullPath(eq < 0 ? arg : arg[..eq]);
            string[] theirFiles = (eq < 0 ? arg : arg[(eq + 1)..]).Split('+', StringSplitOptions.RemoveEmptyEntries);
            FileReport report;
            try
            {
                // ffprobe may be given several files (a presentation whose tracks live in separate media files); their streams follow one another.
                List<ProbeStream> streams = [];
                List<PacketRecord> packets = [];
                foreach (string t in theirFiles)
                {
                    string theirs = Path.GetFullPath(t);
                    int first = streams.Count;
                    List<ProbeStream> s = FfprobeReader.ReadStreams(theirs);
                    foreach (PacketRecord p in FfprobeReader.ReadPackets(theirs, s))
                    {
                        packets.Add(p with { Track = p.Track + first });
                    }

                    streams.AddRange(s.Select(x => x with { Index = x.Index + first }));
                }

                OurResult result = OurReader.Read(registry, ours);
                (streams, packets) = AlignStreams(streams, packets, result.Tracks);
                report = Comparer.Compare(ours, streams, packets, result, exceptions);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                report = new FileReport { File = ours, Status = "error", Message = e.GetType().Name + ": " + e.Message };
            }

            if (report.Status != "ok")
            {
                bad++;
            }

            Reports.WriteFile(outDir, report);
        }

        Console.Write(Reports.Summarize(outDir));
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// ffprobe lists every rendition of a presentation, in an order of its own (media groups first), while we play one rendition per kind in track order.
    /// Pairs each of our tracks with the next unused ffprobe stream of the same kind and drops the rest, renumbering what stays.
    /// </summary>
    private static (List<ProbeStream>, List<PacketRecord>) AlignStreams(List<ProbeStream> streams, List<PacketRecord> packets, List<MediaTrack> tracks)
    {
        Dictionary<int, int> map = [];
        List<ProbeStream> kept = [];
        foreach (MediaTrack t in tracks)
        {
            string kind = t.Kind switch { TrackKind.Video => "video", TrackKind.Audio => "audio", TrackKind.Subtitle => "subtitle", _ => "data" };
            ProbeStream? s = streams.FirstOrDefault(x => x.CodecType == kind && !map.ContainsKey(x.Index));
            if (s is null)
            {
                continue;
            }

            map[s.Index] = kept.Count;
            kept.Add(s with { Index = kept.Count });
        }

        if (kept.Count == 0)
        {
            return (streams, packets);
        }

        return (kept, [.. packets.Where(p => map.ContainsKey(p.Track)).Select(p => p with { Track = map[p.Track] })]);
    }

    // ---- growing files ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>growing FORMAT SECONDS OUTDIR [--fast] [--mem]</c>: ffmpeg writes a SECONDS-long recording (FORMAT is fmp4, mkv, mkvlive, ts or flv) at the
    /// real-time rate while we read the file as it grows. At the end our packets are compared with ffprobe's for the finished file. With --fast the
    /// writer is not slowed to real time; with --mem packets are not kept, only counted and hashed, and the working set is sampled.
    /// </summary>
    public static int Growing(List<string> args, string outDir, string? exceptionsPath)
    {
        string format = args[0];
        int seconds = int.Parse(args[1], CultureInfo.InvariantCulture);
        bool fast = args.Contains("--fast");
        bool mem = args.Contains("--mem");
        Directory.CreateDirectory(outDir);
        string ext = format switch { "fmp4" => "mp4", "mkv" or "mkvlive" => "mkv", "ts" => "ts", "flv" => "flv", _ => throw new ArgumentException("unknown format " + format) };
        string path = Path.Combine(outDir, $"live-{format}.{ext}");
        File.Delete(path);
        string size = mem ? "1280x720" : "320x240";
        string rate = mem ? "-b:v 6M -maxrate 6M -bufsize 12M" : "-b:v 300k";
        List<string> ff = ["-v", "error", "-y"];
        if (!fast)
        {
            ff.Add("-re");
        }

        ff.AddRange(["-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25:duration={seconds}", "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds}"]);
        ff.AddRange(["-c:v", "libx264", "-preset", "ultrafast", .. rate.Split(' '), "-g", "25", "-keyint_min", "25", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k"]);
        ff.AddRange(format switch
        {
            "fmp4" => ["-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof"],
            "mkv" => ["-f", "matroska"],
            "mkvlive" => ["-f", "matroska", "-live", "1"],
            "ts" => ["-f", "mpegts"],
            _ => ["-f", "flv"],
        });
        ff.Add(path);
        using Process writer = Oracle.Start("ffmpeg", ff);
        Task<string> errText = writer.StandardError.ReadToEndAsync();
        _ = writer.StandardOutput.ReadToEndAsync();
        Stopwatch clock = Stopwatch.StartNew();
        GrowthPolicy policy = new() { PollIntervalMilliseconds = 20, IdleTimeoutMilliseconds = 20_000, IsComplete = () => writer.HasExited };
        List<PacketRecord> ours = [];
        long packets = 0;
        long bytes = 0;
        long samplesTaken = 0;
        long minRss = long.MaxValue, maxRss = 0, firstRss = 0, lastRss = 0;
        long minHeap = long.MaxValue, maxHeap = 0;
        long nextSample = 0;
        string fmt;
        MediaTrack[] tracks;
        long allocBefore = 0;
        long checksum = 0;
        using (MediaFile media = MediaFile.OpenGrowing(path, policy))
        {
            fmt = media.Format.Name;
            tracks = [.. media.Tracks];
            IDemuxer demuxer = media.Demuxer!;
            MediaTime[] shift = media.Format == MediaFormatId.IsoBmff ? [.. tracks.Select(OurReader.EditShift)] : [];
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            allocBefore = GC.GetAllocatedBytesForCurrentThread();
            while (demuxer.TryReadPacket(out PacketView view))
            {
                packets++;
                bytes += view.Data.Length;
                PacketInfo i = view.Info;
                if (mem)
                {
                    checksum = unchecked(checksum * 31 + view.Data.Length + (i.IsKeyframe ? 1 : 0));
                    if (clock.ElapsedMilliseconds >= nextSample)
                    {
                        nextSample = clock.ElapsedMilliseconds + 2000;
                        long rss = Process.GetCurrentProcess().WorkingSet64;
                        long heap = GC.GetTotalMemory(false);
                        firstRss = samplesTaken == 0 ? rss : firstRss;
                        lastRss = rss;
                        samplesTaken++;
                        if (samplesTaken > 3)
                        {
                            minRss = Math.Min(minRss, rss);
                            maxRss = Math.Max(maxRss, rss);
                            minHeap = Math.Min(minHeap, heap);
                            maxHeap = Math.Max(maxHeap, heap);
                        }
                    }

                    continue;
                }

                SHA256.HashData(view.Data, digest);
                if (shift.Length > 0)
                {
                    MediaTime s = shift[i.TrackIndex];
                    i = i with { Pts = OurReader.Shifted(i.Pts, s), Dts = OurReader.Shifted(i.Dts, s) };
                }

                ours.Add(new PacketRecord(i.TrackIndex, i.Pts, i.Dts, i.Duration, view.Data.Length, i.SourceRange.IsNone ? -1 : i.SourceRange.Offset, i.IsKeyframe, Convert.ToHexStringLower(digest)));
            }

            shiftHolder = shift;
        }

        writer.WaitForExit();
        string fileInfo = $"{new FileInfo(path).Length} bytes written by ffmpeg in {clock.Elapsed.TotalSeconds:F1} s (exit {writer.ExitCode})";
        Console.WriteLine($"growing {format}: format {fmt}, {packets} packets, {bytes} payload bytes, {fileInfo}");
        if (mem)
        {
            Console.WriteLine($"memory: working set first {firstRss >> 20} MiB, min {minRss >> 20} MiB, max {maxRss >> 20} MiB, last {lastRss >> 20} MiB (after the first 3 samples; {samplesTaken} samples); managed heap min {minHeap >> 20} MiB max {maxHeap >> 20} MiB; checksum {checksum}");
            File.Delete(path);
            return writer.ExitCode == 0 ? 0 : 1;
        }

        Exceptions exceptions = exceptionsPath is null ? Exceptions.None : Exceptions.Load(exceptionsPath);
        List<ProbeStream> streams = FfprobeReader.ReadStreams(path);
        List<PacketRecord> theirs = [.. FfprobeReader.ReadPackets(path, streams)];
        OurResult result = new() { Format = fmt, Tracks = [.. tracks], Packets = ours, Shifts = shiftHolder };
        FileReport report = Comparer.Compare(path, streams, theirs, result, exceptions);
        Reports.WriteFile(outDir, report);
        Console.WriteLine($"growing {format}: ffprobe {report.TheirPackets} packets, ours {report.OurPackets}, status {report.Status}, differences {string.Join(", ", report.Differences.Select(d => d.Key + "=" + d.Value))}, excepted {string.Join(", ", report.Excepted.Select(d => d.Key + "=" + d.Value))}");
        foreach (FieldDifference d in report.Examples.Take(6))
        {
            Console.WriteLine($"  track {d.Track} packet {d.PacketIndex} {d.Field}: ffprobe {d.Theirs}, ours {d.Ours}");
        }

        File.Delete(path);
        return report.Status == "ok" ? 0 : 1;
    }

    private static MediaTime[] shiftHolder = [];

    // ---- I/O --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>presio PLAYLIST...</c> (paths under <c>--root</c>): bytes and requests to open each presentation and to read all of it, through the local range
    /// server, for ffprobe and for our reader with an HTTP resolver whose first read of a resource fetches its first 256 KiB.
    /// </summary>
    public static int PresentationIo(List<string> playlists, string root, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using RangeServer server = new(root);
        using HttpClient client = new();
        StringBuilder tsv = new("presentation\ttool\tmode\tbytes\trequests\tsource-reads\tnote\n");
        foreach (string playlist in playlists)
        {
            string rel = Path.GetRelativePath(root, Path.GetFullPath(playlist)).Replace('\\', '/');
            string url = server.UrlFor(rel);
            int segments = File.ReadLines(playlist).Count(l => l.Length > 0 && l[0] != '#' || l.Contains("<S ", StringComparison.Ordinal));

            ServerCounters c = server.Begin();
            Oracle.Run("ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", url);
            Thread.Sleep(300);
            Row(tsv, rel, "ffprobe", "open", c.BodyBytes, c.Requests, 0, $"{segments} segment entries");

            c = server.Begin();
            Oracle.Run("ffprobe", "-v", "error", "-show_packets", "-of", "compact", url);
            Thread.Sleep(300);
            Row(tsv, rel, "ffprobe", "read-all", c.BodyBytes, c.Requests, 0, "");

            CountingObserver observer = new();
            DelegatePresentationResolver resolver = new(async (uri, ct) => new ObservedMediaSource(null, await HttpRangeSource.OpenAsync(client, uri, ct).ConfigureAwait(false), observer));
            c = server.Begin();
            using MediaFile media = MediaFile.OpenPresentationAsync(new Uri(url), resolver).AsTask().GetAwaiter().GetResult();
            Thread.Sleep(300);
            Row(tsv, rel, "ours", "open", c.BodyBytes, c.Requests, observer.Calls, $"{media.Tracks.Count} tracks");

            c = server.Begin();
            long n = 0;
            foreach (MediaTrack t in media.Tracks)
            {
                _ = t;
            }

            IAsyncDemuxer d = media.AsyncDemuxer!;
            while (d.ReadPacketAsync().AsTask().GetAwaiter().GetResult())
            {
                n++;
            }

            Thread.Sleep(300);
            Row(tsv, rel, "ours", "read-all", c.BodyBytes, c.Requests, observer.Calls, $"{n} packets");
        }

        File.WriteAllText(Path.Combine(outDir, "presentation-io.tsv"), tsv.ToString());
        Console.Write(tsv);
        return 0;
    }

    private static void Row(StringBuilder tsv, string name, string tool, string mode, long bytes, long requests, long reads, string note) =>
        tsv.Append(CultureInfo.InvariantCulture, $"{name}\t{tool}\t{mode}\t{bytes}\t{requests}\t{reads}\t{note}\n");

    /// <summary>A ranged source over HTTP. The first read of a resource fetches its first 256 KiB with one request, which also says how long it is.</summary>
    private sealed class HttpRangeSource : IAsyncMediaSource
    {
        private const int Prefetch = 256 * 1024;
        private readonly HttpClient _client;
        private readonly Uri _uri;
        private byte[] _cache = [];

        private HttpRangeSource(HttpClient client, Uri uri, long length)
        {
            _client = client;
            _uri = uri;
            Length = length;
        }

        public long Length { get; }

        public static async ValueTask<HttpRangeSource> OpenAsync(HttpClient client, Uri uri, CancellationToken ct)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, uri);
            req.Headers.Range = new RangeHeaderValue(0, Prefetch - 1);
            using HttpResponseMessage resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            byte[] body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            long length = resp.Content.Headers.ContentRange?.Length ?? body.Length;
            return new HttpRangeSource(client, uri, length) { _cache = body };
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset >= Length)
            {
                return 0;
            }

            int want = (int)Math.Min(destination.Length, Length - offset);
            if (offset + want <= _cache.Length)
            {
                _cache.AsMemory((int)offset, want).CopyTo(destination);
                return want;
            }

            using HttpRequestMessage req = new(HttpMethod.Get, _uri);
            req.Headers.Range = new RangeHeaderValue(offset, offset + want - 1);
            using HttpResponseMessage resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using Stream body = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int total = 0;
            while (total < want)
            {
                int n = await body.ReadAsync(destination[total..want], cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        public ValueTask DisposeAsync() => default;
    }
}
