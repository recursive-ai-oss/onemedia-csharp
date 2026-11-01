using System.Globalization;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Editing;

internal static class EditFiles
{
    public static IReadOnlyList<ClipReport> Write(string input, IReadOnlyList<string> outputs, Func<IsoBmffDemuxer, Stream[], IReadOnlyList<ClipReport>> run,
        Func<MatroskaDemuxer, Stream[], IReadOnlyList<ClipReport>> runMatroska)
    {
        using IDisposable d = OpenAny(input);
        string[] partial = new string[outputs.Count];
        FileStream[] streams = new FileStream[outputs.Count];
        for (int i = 0; i < partial.Length; i++)
        {
            partial[i] = outputs[i] + ".partial";
            streams[i] = new FileStream(partial[i], FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        }

        try
        {
            IReadOnlyList<ClipReport> reports = d is IsoBmffDemuxer iso ? run(iso, streams) : runMatroska((MatroskaDemuxer)d, streams);
            foreach (FileStream s in streams)
            {
                s.Dispose();
            }

            for (int i = 0; i < outputs.Count; i++)
            {
                File.Move(partial[i], outputs[i], overwrite: true);
            }

            return reports;
        }
        catch
        {
            foreach (FileStream s in streams)
            {
                s.Dispose();
            }

            foreach (string p in partial)
            {
                File.Delete(p);
            }

            throw;
        }
    }

    public static IReadOnlyList<ClipReport> WriteParts(string input, string pattern, Func<IsoBmffDemuxer, Func<int, Stream>, IReadOnlyList<ClipReport>> run,
        Func<MatroskaDemuxer, Func<int, Stream>, IReadOnlyList<ClipReport>> runMatroska)
    {
        using IDisposable d = OpenAny(input);
        List<(FileStream Stream, string Partial, string Final)> parts = [];
        try
        {
            Func<int, Stream> open = index =>
            {
                string final = PartPath(pattern, index + 1);
                string partial = final + ".partial";
                FileStream fs = new(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                parts.Add((fs, partial, final));
                return fs;
            };
            IReadOnlyList<ClipReport> reports = d is IsoBmffDemuxer iso ? run(iso, open) : runMatroska((MatroskaDemuxer)d, open);
            foreach ((FileStream fs, _, _) in parts)
            {
                fs.Dispose();
            }

            foreach ((_, string partial, string final) in parts)
            {
                File.Move(partial, final, overwrite: true);
            }

            return reports;
        }
        catch
        {
            foreach ((FileStream fs, string partial, _) in parts)
            {
                fs.Dispose();
                File.Delete(partial);
            }

            throw;
        }
    }

    public static ClipReport Join(string[] inputs, string output, CancellationToken ct)
    {
        List<IDisposable> sources = [];
        string partial = output + ".partial";
        try
        {
            foreach (string path in inputs)
            {
                sources.Add(OpenAny(path));
            }

            using FileStream fs = new(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            ClipReport report;
            if (sources[0] is IsoBmffDemuxer)
            {
                List<IsoBmffDemuxer> iso = [];
                foreach (IDisposable s in sources)
                {
                    iso.Add(AsIso(s));
                }

                report = IsoJoiner.Concat(iso, fs, null, ct);
            }
            else
            {
                List<MatroskaDemuxer> mkv = [];
                foreach (IDisposable s in sources)
                {
                    mkv.Add(AsMatroska(s));
                }

                report = MatroskaJoiner.Concat(mkv, fs, null, ct);
            }

            fs.Dispose();
            File.Move(partial, output, overwrite: true);
            return report;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
        finally
        {
            foreach (IDisposable s in sources)
            {
                s.Dispose();
            }
        }
    }

    private static IsoBmffDemuxer AsIso(IDisposable d) =>
        d as IsoBmffDemuxer ?? throw new EditRefusedException(EditRefusalReason.NeedsReencode, "The inputs are in different container formats; join files of one format (MP4/MOV/3GP, or Matroska/WebM).");

    private static MatroskaDemuxer AsMatroska(IDisposable d) =>
        d as MatroskaDemuxer ?? throw new EditRefusedException(EditRefusalReason.NeedsReencode, "The inputs are in different container formats; join files of one format (MP4/MOV/3GP, or Matroska/WebM).");

    private static string PartPath(string pattern, int number)
    {
        if (pattern.Contains("{0", StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.InvariantCulture, pattern, number);
        }

        string ext = Path.GetExtension(pattern);
        return string.Concat(pattern.AsSpan(0, pattern.Length - ext.Length), "-", number.ToString("000", CultureInfo.InvariantCulture), ext);
    }

    private static IDisposable OpenAny(string path)
    {
        FileMediaSource source = FileMediaSource.Open(path);
        if (IsoBmffDemuxer.TryOpen(source, new MediaOpenOptions(), out IsoBmffDemuxer? d, out MediaDefect failure) && d is not null)
        {
            return d;
        }

        try
        {
            return MatroskaDemuxer.Open(source);
        }
        catch (MediaException)
        {
            source.Dispose();
            throw new EditRefusedException(EditRefusalReason.UnsupportedSource, $"Cutting and joining work on MP4, MOV, 3GP, Matroska and WebM files; {Path.GetFileName(path)} did not open as one ({failure.Message}).");
        }
    }
}
