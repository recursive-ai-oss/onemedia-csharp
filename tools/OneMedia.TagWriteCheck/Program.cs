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
                case "mapping":
                    Console.Write(TagMappingTable.RenderMarkdown());
                    return 0;
                case "writemap":
                    Console.Write(WriteMap());
                    return 0;
                case "mkvdefects":
                    MkvDefects(args[1]);
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

    // The key each system writes for each common field, from the same table the writers use.
    private static string WriteMap()
    {
        string[] systems = [TagSystems.ITunes, TagSystems.QuickTimeKeys, TagSystems.QuickTimeUserData, TagSystems.Matroska, TagSystems.Id3v2, TagSystems.Ape, TagSystems.VorbisComment, TagSystems.RiffInfo, TagSystems.Aiff];
        StringBuilder sb = new();
        sb.Append("| Field | ").Append(string.Join(" | ", systems)).Append(" |\n|---|").Append(string.Concat(systems.Select(_ => "---|"))).Append('\n');
        foreach (CommonField f in Enum.GetValues<CommonField>())
        {
            sb.Append("| ").Append(f);
            foreach (string sys in systems)
            {
                string? key = sys == TagSystems.QuickTimeUserData ? UserDataAtom(f) : TagMappingTable.Rows.FirstOrDefault(r => r.Field == f && r.System == sys)?.Keys[0];
                sb.Append(" | ").Append(key is null ? "—" : "`" + key + "`");
            }

            sb.Append(" |\n");
        }

        return sb.ToString();
    }

    private static string? UserDataAtom(CommonField f) => f switch
    {
        CommonField.Title => "©nam", CommonField.Artist => "©ART", CommonField.Album => "©alb", CommonField.Composer => "©wrt", CommonField.Genre => "©gen",
        CommonField.Comment => "©cmt", CommonField.Date => "©day", CommonField.Copyright => "©cpy", CommonField.Encoder => "©too", CommonField.EncodedBy => "©enc",
        CommonField.Description => "©inf", CommonField.Director => "©dir", CommonField.Author => "©aut", CommonField.Keywords => "©key", _ => null,
    };

    private static CommonField? RemovableField(string path)
    {
        using MediaFile m = Media.Open(path);
        // A Matroska file's Encoder also comes from the Segment's WritingApp, which belongs to the muxer and is kept, so it cannot show a removal.
        bool matroska = Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mka" or ".webm";
        foreach (CommonField f in new[] { CommonField.Genre, CommonField.Date, CommonField.Copyright, CommonField.Composer, CommonField.Album, CommonField.Encoder, CommonField.Grouping })
        {
            if (matroska && f == CommonField.Encoder)
            {
                continue;
            }

            if (m.Tags.First(f) is not null)
            {
                return f;
            }
        }

        return null;
    }

    // ASF has no comment field: its Content Description's "description" is the closest, and the mapping table calls it Description.
    private static CommonField CommentField(string path) => Path.GetExtension(path).ToLowerInvariant() is ".wma" or ".wmv" or ".asf" ? CommonField.Description : CommonField.Comment;

    private static string? CommentOf(MediaFile m, string path) => CommentField(path) == CommonField.Description ? m.Tags.Description : m.Tags.Comment;

    private static string Edit(string input, string output)
    {
        CommonField? remove = Environment.GetEnvironmentVariable("TAGCHECK_NOREMOVE") == "1" ? null : RemovableField(input);
        TagEdits e = new();
        e.Set(CommonField.Title, Environment.GetEnvironmentVariable("TAGCHECK_TITLE") ?? "Edited Title \u00fc");
        e.Set(CommonField.Artist, "Edited Artist");
        e.Set(CommentField(input), "An edited comment");
        e.SetPicture(Png, "image/png");
        if (remove is { } r)
        {
            e.Remove(r);
        }

        bool inMemory = Environment.GetEnvironmentVariable("TAGCHECK_MEMORY") == "1";
        using MemoryMediaSource? planSource = inMemory ? new(File.ReadAllBytes(input)) : null;
        TagWritePlan plan = planSource is not null ? TagWriter.Plan(planSource, e) : TagWriter.Plan(input, e);
        File.Delete(output);
        plan.Report.Write = SafeWriter.SaveAs(plan.Plan, input, output, true);
        string copies = VerifyCopies(plan.Plan, input, output);
        string before = Hash(input);
        string after = Hash(output);
        using MemoryMediaSource? mem = inMemory ? new(File.ReadAllBytes(output)) : null;
        using MediaFile m = mem is not null ? MediaFile.Open(mem) : Media.Open(output);
        string readBack = $"title={Flag(m.Tags.Title == "Edited Title \u00fc")} artist={Flag(m.Tags.Artist == "Edited Artist")} comment={Flag(CommentOf(m, input) == "An edited comment")} picture={(Path.GetExtension(input).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "none(webm)" : Flag(m.Tags.Pictures.Any(p => p.Length == Png.Length)))} removed={(remove is { } rr ? Flag(m.Tags.First(rr) is null) + ":" + rr : "none")}";
        long changed = plan.Plan.Pieces.Where(p => p.Kind != OutputPieceKind.Copy).Sum(p => p.Length);
        string systems = string.Join(",", plan.Report.Systems.Select(s => s.System + (s.Created ? "+" : "")));
        string notRep = string.Join(",", plan.Report.Systems.SelectMany(s => s.NotRepresentable.Select(n => s.System + ":" + n.Field)));
        string ranges = string.Join(";", plan.Plan.Changes.Take(12).Select(c => $"{c.Description}[{c.Source.Offset}+{c.Source.Length}->{c.Output.Offset}+{c.Output.Length}]"));
        return string.Join('\t', "EDIT", input, new FileInfo(input).Length, new FileInfo(output).Length, readBack, "payload=" + Flag(before == after) + (before == after ? "" : ":" + before.Length + "/" + after.Length), "copies=" + copies, "changedBytes=" + changed, "sameLayout=" + plan.Plan.IsSameLayout, "systems=" + systems, "notRep=" + notRep, "ranges=" + ranges);
    }

    // Diagnostic: the defects the Matroska reader logs for the Segment's top level, with the first Cluster's offset.
    private static void MkvDefects(string path)
    {
        using FileMediaSource src = FileMediaSource.Open(path);
        RecursiveAI.OneMedia.Matroska.EbmlDocument doc = RecursiveAI.OneMedia.Matroska.EbmlDocument.Open(src);
        RecursiveAI.OneMedia.Matroska.EbmlElement seg = doc.Segment!;
        long first = seg.ChildElements.FirstOrDefault(c => c.Id == 0x1F43B675)?.Offset ?? -1;
        Console.WriteLine($"{path}\tsegment@{seg.Offset} firstCluster@{first}");
        foreach (MediaDefect d in doc.Defects)
        {
            Console.WriteLine("  " + d);
        }
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
            small.Set(CommentField(input), "c");
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
            big.Set(CommentField(input), new string('x', 3 * 1024 * 1024));
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
