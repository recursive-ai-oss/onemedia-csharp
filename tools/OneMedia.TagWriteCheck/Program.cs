using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags;

// Commands (one result line per file, tab separated):
//   edit <in> <out>        the fixed edit set (title, artist, comment, cover art, one field removed), SaveAs, then checks
//   inplace <in> <dir>     a same-size-or-smaller edit with SaveInPlace on a copy, then an oversized edit that must be refused
//   hash <file>            per-track payload hash read through the demuxer
return Checker.Run(args);

internal static class Checker
{
    // A 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public static int Run(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "edit":
                    Console.WriteLine(Edit(args[1], args[2]));
                    return 0;
                case "inplace":
                    Console.WriteLine(InPlace(args[1], args[2]));
                    return 0;
                case "hash":
                    Console.WriteLine(Hash(args[1]));
                    return 0;
                default:
                    return 2;
            }
        }
        catch (Exception e) when (e is MediaException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"ERROR\t{args.ElementAtOrDefault(1)}\t{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
            return 1;
        }
    }

    private static CommonField? RemovableField(string path)
    {
        using MediaFile m = Media.Open(path);
        foreach (CommonField f in new[] { CommonField.Genre, CommonField.Date, CommonField.Copyright, CommonField.Composer, CommonField.Album, CommonField.Encoder, CommonField.Grouping })
        {
            if (m.Tags.First(f) is not null)
            {
                return f;
            }
        }

        return null;
    }

    private static string Edit(string input, string output)
    {
        CommonField? remove = Environment.GetEnvironmentVariable("TAGCHECK_NOREMOVE") == "1" ? null : RemovableField(input);
        TagEdits e = new();
        e.Set(CommonField.Title, Environment.GetEnvironmentVariable("TAGCHECK_TITLE") ?? "Edited Title \u00fc");
        e.Set(CommonField.Artist, "Edited Artist");
        e.Set(CommonField.Comment, "An edited comment");
        e.SetPicture(Png, "image/png");
        if (remove is { } r)
        {
            e.Remove(r);
        }

        using MemoryMediaSource planSource = new(File.ReadAllBytes(input));
        TagWritePlan plan = Environment.GetEnvironmentVariable("TAGCHECK_MEMORY") == "1" ? TagWriter.Plan(planSource, e) : TagWriter.Plan(input, e);
        File.Delete(output);
        plan.Report.Write = SafeWriter.SaveAs(plan.Plan, input, output, true);
        string copies = VerifyCopies(plan.Plan, input, output);
        string before = Hash(input);
        string after = Hash(output);
        using MemoryMediaSource mem = new(File.ReadAllBytes(output));
        using MediaFile m = Environment.GetEnvironmentVariable("TAGCHECK_MEMORY") == "1" ? MediaFile.Open(mem) : Media.Open(output);
        string readBack = $"title={Flag(m.Tags.Title == "Edited Title \u00fc")} artist={Flag(m.Tags.Artist == "Edited Artist")} comment={Flag(m.Tags.Comment == "An edited comment")} picture={Flag(m.Tags.Pictures.Any(p => p.Length == Png.Length))} removed={(remove is { } rr ? Flag(m.Tags.First(rr) is null) + ":" + rr : "none")}";
        long changed = plan.Plan.Pieces.Where(p => p.Kind != OutputPieceKind.Copy).Sum(p => p.Length);
        string systems = string.Join(",", plan.Report.Systems.Select(s => s.System + (s.Created ? "+" : "")));
        string notRep = string.Join(",", plan.Report.Systems.SelectMany(s => s.NotRepresentable.Select(n => s.System + ":" + n.Field)));
        string ranges = string.Join(";", plan.Plan.Changes.Take(12).Select(c => $"{c.Description}[{c.Source.Offset}+{c.Source.Length}->{c.Output.Offset}+{c.Output.Length}]"));
        return string.Join('\t', "EDIT", input, new FileInfo(input).Length, new FileInfo(output).Length, readBack, "payload=" + Flag(before == after) + (before == after ? "" : ":" + before.Length + "/" + after.Length), "copies=" + copies, "changedBytes=" + changed, "sameLayout=" + plan.Plan.IsSameLayout, "systems=" + systems, "notRep=" + notRep, "ranges=" + ranges);
    }

    private static string Flag(bool ok) => ok ? "ok" : "FAIL";

    private static string VerifyCopies(EditPlan plan, string input, string output)
    {
        using FileStream a = File.OpenRead(input);
        using FileStream b = File.OpenRead(output);
        if (b.Length != plan.Length)
        {
            return $"FAIL(length {b.Length} != {plan.Length})";
        }

        long outPos = 0;
        byte[] x = new byte[1 << 16];
        byte[] y = new byte[1 << 16];
        foreach (OutputPiece p in plan.Pieces)
        {
            if (p.Kind == OutputPieceKind.Copy)
            {
                for (long done = 0; done < p.Length;)
                {
                    int n = (int)Math.Min(x.Length, p.Length - done);
                    a.Position = p.SourceRange.Offset + done;
                    b.Position = outPos + done;
                    a.ReadExactly(x, 0, n);
                    b.ReadExactly(y, 0, n);
                    if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n)))
                    {
                        return $"FAIL(at {outPos + done})";
                    }

                    done += n;
                }
            }

            outPos += p.Length;
        }

        return "ok";
    }

    private static string Hash(string path)
    {
        using MediaFile m = Media.Open(path);
        IDemuxer d = m.Demuxer ?? throw new InvalidOperationException("no sync demuxer");
        Dictionary<int, IncrementalHash> hashes = [];
        Dictionary<int, long> counts = [];
        try
        {
            while (d.TryReadPacket(out PacketView v))
            {
                if (!hashes.TryGetValue(v.Info.TrackIndex, out IncrementalHash? h))
                {
                    hashes[v.Info.TrackIndex] = h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    counts[v.Info.TrackIndex] = 0;
                }

                h.AppendData(v.Data);
                counts[v.Info.TrackIndex]++;
            }

            StringBuilder sb = new();
            foreach (int k in hashes.Keys.Order())
            {
                sb.Append(k).Append(':').Append(counts[k]).Append(':').Append(Convert.ToHexString(hashes[k].GetHashAndReset())[..16]).Append(' ');
            }

            return sb.ToString().TrimEnd();
        }
        finally
        {
            foreach (IncrementalHash h in hashes.Values)
            {
                h.Dispose();
            }
        }
    }

    private static string InPlace(string input, string dir)
    {
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, Path.GetFileName(input));
        File.Copy(input, copy, true);
        string before = Hash(copy);
        string result;
        try
        {
            TagEdits small = new();
            small.Set(CommonField.Title, "T");
            small.Set(CommonField.Comment, "c");
            TagWriteReport r = TagWriter.SaveInPlace(copy, small);
            result = "small=ok(" + r.Write!.BytesWritten + "B)";
        }
        catch (PaddingRequiredException e)
        {
            result = $"small=PaddingRequired(need {e.Required}, have {e.Available})";
        }
        catch (UnsupportedMediaException e)
        {
            result = "small=unsupported:" + e.Message.Split('.')[0];
        }

        string after = Hash(copy);
        try
        {
            TagEdits big = new();
            big.Set(CommonField.Comment, new string('x', 3 * 1024 * 1024));
            TagWriter.SaveInPlace(copy, big);
            result += "\tbig=ok(!)";
        }
        catch (PaddingRequiredException)
        {
            result += "\tbig=PaddingRequired";
        }
        catch (UnsupportedMediaException e)
        {
            result += "\tbig=unsupported:" + e.Message.Split('.')[0];
        }

        bool intact = new FileInfo(copy).Length == new FileInfo(input).Length || result.Contains("small=ok", StringComparison.Ordinal);
        File.Delete(copy);
        return string.Join('\t', "INPLACE", input, result, "payload=" + Flag(before == after), "sizeKept=" + Flag(intact));
    }
}
