using System.Globalization;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Editing;

internal static class EditFiles
{
    public static IReadOnlyList<ClipReport> Write(string input, IReadOnlyList<string> outputs, Func<IsoBmffDemuxer, Stream[], IReadOnlyList<ClipReport>> run)
    {
        using IsoBmffDemuxer d = OpenIso(input);
        string[] partial = new string[outputs.Count];
        FileStream[] streams = new FileStream[outputs.Count];
        for (int i = 0; i < partial.Length; i++)
        {
            partial[i] = outputs[i] + ".partial";
            streams[i] = new FileStream(partial[i], FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        }

        try
        {
            IReadOnlyList<ClipReport> reports = run(d, streams);
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

    public static IReadOnlyList<ClipReport> WriteParts(string input, string pattern, Func<IsoBmffDemuxer, Func<int, Stream>, IReadOnlyList<ClipReport>> run)
    {
        using IsoBmffDemuxer d = OpenIso(input);
        List<(FileStream Stream, string Partial, string Final)> parts = [];
        try
        {
            IReadOnlyList<ClipReport> reports = run(d, index =>
            {
                string final = PartPath(pattern, index + 1);
                string partial = final + ".partial";
                FileStream fs = new(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                parts.Add((fs, partial, final));
                return fs;
            });
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
        List<IsoBmffDemuxer> sources = [];
        string partial = output + ".partial";
        try
        {
            foreach (string path in inputs)
            {
                sources.Add(OpenIso(path));
            }

            using FileStream fs = new(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            ClipReport report = IsoJoiner.Concat(sources, fs, null, ct);
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
            foreach (IsoBmffDemuxer s in sources)
            {
                s.Dispose();
            }
        }
    }

    private static string PartPath(string pattern, int number)
    {
        if (pattern.Contains("{0", StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.InvariantCulture, pattern, number);
        }

        string ext = Path.GetExtension(pattern);
        return string.Concat(pattern.AsSpan(0, pattern.Length - ext.Length), "-", number.ToString("000", CultureInfo.InvariantCulture), ext);
    }

    private static IsoBmffDemuxer OpenIso(string path)
    {
        FileMediaSource source = FileMediaSource.Open(path);
        if (!IsoBmffDemuxer.TryOpen(source, new MediaOpenOptions(), out IsoBmffDemuxer? d, out MediaDefect failure) || d is null)
        {
            source.Dispose();
            throw new EditRefusedException(EditRefusalReason.UnsupportedSource, $"Cutting and joining work on MP4, MOV and 3GP files; {Path.GetFileName(path)} did not open as one ({failure.Message}).");
        }

        return d;
    }
}
