using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Riff;

// Commands (one result line per file, tab separated):
//   roundtrip [--no-strict] <file|@list>...   untouched files through SaveAs, plain and with every header re-emitted
//   edit <in> <out>                           a minimal metadata edit, SaveAs, copy pieces verified against the input
//   hash <file>                               per-track payload hash read through the demuxer
//   inplace <in> <workdir>                    pad, edit within the padding in place, then refuse a too-large edit
//   save <file>                               the same minimal edit applied with an atomic Save (for the kill test)
return Checker.Run(args);

internal static class Checker
{
    private enum Family { Unknown, Iso, Matroska, Riff }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: roundtrip|edit|hash|inplace|save ...");
            return 2;
        }

        int code = 0;
        try
        {
            code = args[0] switch
            {
                "roundtrip" => RoundTrip(args[1..]),
                "edit" => Edit(args[1], args[2]),
                "hash" => Hash(args[1]),
                "inplace" => InPlace(args[1], args[2]),
                "save" => Save(args[1]),
                _ => 2,
            };
        }
        catch (Exception e) when (e is MediaException or IOException or ArgumentException or InvalidOperationException)
        {
            Console.WriteLine($"ERROR\t{e.GetType().Name}: {e.Message} {e.StackTrace?.Split((char)10)[1]}");
            code = 1;
        }

        Console.Error.WriteLine($"peakWorkingSetMiB={Process.GetCurrentProcess().PeakWorkingSet64 >> 20}");
        return code;
    }

    private static Family Detect(string path)
    {
        using FileMediaSource s = FileMediaSource.Open(path);
        Span<byte> head = new byte[48];
        int n = s.Read(0, head);
        head = head[..n];
        if (n >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        {
            return Family.Matroska;
        }

        if (n >= 12 && (head[..4].SequenceEqual("RIFF"u8) || head[..4].SequenceEqual("RF64"u8) || head[..4].SequenceEqual("BW64"u8) || head[..4].SequenceEqual("FORM"u8)))
        {
            return Family.Riff;
        }

        if (n >= 40 && new Guid(head[..16]) == new Guid("66666972-912E-11CF-A5D6-28DB04C10000"))
        {
            return Family.Riff;
        }

        if (n >= 8)
        {
            uint type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            if (type is 0x66747970 or 0x6D6F6F76 or 0x6D646174 or 0x66726565 or 0x736B6970 or 0x77696465 or 0x73747970 or 0x6D6F6F66 or 0x73696478)
            {
                return Family.Iso;
            }
        }

        return Family.Unknown;
    }

    private static string Sha256(string path)
    {
        using FileStream f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    private static IEnumerable<string> Expand(string[] args)
    {
        foreach (string a in args)
        {
            if (a.StartsWith('@'))
            {
                foreach (string line in File.ReadLines(a[1..]))
                {
                    if (line.Length > 0)
                    {
                        yield return line;
                    }
                }
            }
            else
            {
                yield return a;
            }
        }
    }

    // ---- round trip ----

    private static int RoundTrip(string[] args)
    {
        bool strict = !args.Contains("--no-strict");
        string tmpDir = Path.GetTempPath();
        foreach (string path in Expand([.. args.Where(a => a != "--no-strict")]))
        {
            string tmp = Path.Combine(tmpDir, $"wc-{Environment.ProcessId}.tmp");
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                Family family = Detect(path);
                if (family == Family.Unknown)
                {
                    Console.WriteLine($"SKIP\t{path}\tnot a family with a writer");
                    continue;
                }

                string original = Sha256(path);
                long length = new FileInfo(path).Length;
                using FileMediaSource src = FileMediaSource.Open(path);
                EditPlan plain = PlanFor(family, src, strictHeaders: false, edit: false);
                SafeWriter.SaveAs(plain, path, tmp, overwrite: true);
                bool plainOk = Sha256(tmp) == original && plain.IsSameLayout;
                string strictOk = "-";
                if (strict)
                {
                    EditPlan s = PlanFor(family, src, strictHeaders: true, edit: false);
                    SafeWriter.SaveAs(s, path, tmp, overwrite: true);
                    strictOk = Sha256(tmp) == original ? "identical" : "DIFFERENT";
                }

                Console.WriteLine($"{(plainOk && strictOk != "DIFFERENT" ? "OK" : "FAIL")}\t{family}\t{path}\t{length}\tsaveAs={(plainOk ? "identical" : "DIFFERENT")}\tstrict={strictOk}\t{sw.ElapsedMilliseconds}ms");
            }
            catch (Exception e) when (e is MediaException or IOException or ArgumentException or InvalidOperationException)
            {
                Console.WriteLine($"ERROR\t{path}\t{e.GetType().Name}: {e.Message}");
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        return 0;
    }

    private static int InputErrors;

    private static EditPlan PlanFor(Family family, FileMediaSource src, bool strictHeaders, bool edit)
    {
        switch (family)
        {
            case Family.Iso:
                {
                    BoxTree tree = new(src);
                    BoxEditor e = new(tree, src) { RewriteHeaders = strictHeaders };
                    if (edit)
                    {
                        BasicIso(tree, e);
                    }

                    EditPlan plan = e.Build();
                    InputErrors = tree.Defects.Count(d => d.Severity == DefectSeverity.Error);
                    return plan;
                }

            case Family.Matroska:
                {
                    EbmlDocument doc = EbmlDocument.Open(src);
                    EbmlEditor e = new(doc, src) { RewriteHeaders = strictHeaders };
                    if (edit)
                    {
                        BasicMatroska(doc, e);
                    }

                    EditPlan plan = e.Build();
                    InputErrors = doc.Defects.Count(d => d.Severity == DefectSeverity.Error);
                    return plan;
                }

            default:
                {
                    DefectLog log = new();
                    ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, log) ?? throw new MediaFormatException("Not a chunk file.");
                    ChunkEditor e = new(cf, src) { RewriteHeaders = strictHeaders };
                    if (edit)
                    {
                        BasicRiff(cf, e);
                    }

                    EditPlan plan = e.Build();
                    InputErrors = log.Count(d => d.Severity >= DefectSeverity.Warning);
                    return plan;
                }
        }
    }

    // ---- the minimal metadata edits ----

    private static byte[] IlstUdta(string title)
    {
        byte[] text = Encoding.UTF8.GetBytes(title);
        byte[] dataPayload = new byte[8 + text.Length];
        dataPayload[3] = 1; // well-known type 1: UTF-8 text
        text.CopyTo(dataPayload, 8);
        byte[] data = BoxBuilder.Create(BoxTypes.Make("data"), dataPayload);
        byte[] item = BoxBuilder.Create(BoxTypes.Make("\u00a9nam"), data);
        byte[] ilst = BoxBuilder.Create(BoxTypes.Ilst, item);
        byte[] hdlr = BoxBuilder.CreateFull(BoxTypes.Hdlr, 0, 0, new byte[21]);
        byte[] meta = BoxBuilder.CreateFull(BoxTypes.Meta, 0, 0, [.. hdlr, .. ilst]);
        return BoxBuilder.Create(BoxTypes.Udta, meta);
    }

    private static byte[] MatroskaTags(string title)
    {
        byte[] simple = EbmlWriter.Element(0x67C8, [.. EbmlWriter.Element(0x45A3, "TITLE"u8), .. EbmlWriter.Element(0x4487, Encoding.UTF8.GetBytes(title))]);
        byte[] targets = EbmlWriter.Element(0x63C0, []); // a Tag needs its Targets element, even an empty one (applies to the whole file)
        return EbmlWriter.Element(TagsId, EbmlWriter.Element(0x7373, [.. targets, .. simple]));
    }

    private const uint TagsId = 0x1254C367;

    private static readonly Guid CustomW64 = new("6D656461-4F6E-4D65-8000-000000000001");

    private static byte[] RiffMeta(ChunkLayout layout, string title)
    {
        byte[] text = [.. Encoding.UTF8.GetBytes(title), 0];
        return layout switch
        {
            ChunkLayout.Iff => ChunkBuilder.Create(layout, "NAME", Encoding.UTF8.GetBytes(title)),
            ChunkLayout.Wave64 => ChunkBuilder.CreateWave64(CustomW64, text),
            _ => ChunkBuilder.CreateList(layout, "INFO", ChunkBuilder.Create(layout, "INAM", text)),
        };
    }

    private static bool IsRiffMeta(ChunkLayout layout, ChunkNode c) => layout switch
    {
        ChunkLayout.Iff => c.Id == "NAME",
        ChunkLayout.Wave64 => c.ChunkGuid == CustomW64,
        _ => c.Id == "LIST" && c.FormType == "INFO",
    };

    private static void BasicIso(BoxTree tree, BoxEditor e)
    {
        BoxNode moov = tree.Find(BoxTypes.Moov) ?? throw new MediaFormatException("No moov box.");
        BoxNode? udta = moov.Children.FirstOrDefault(c => c.Type == BoxTypes.Udta);
        if (udta is not null)
        {
            e.Replace(udta, IlstUdta("Deposition, day 2"));
        }
        else
        {
            e.AppendChild(moov, IlstUdta("Deposition, day 2"));
        }
    }

    private static void BasicMatroska(EbmlDocument doc, EbmlEditor e)
    {
        EbmlElement seg = doc.Segment ?? throw new MediaFormatException("No Segment.");
        EbmlElement? info = seg.ChildElements.FirstOrDefault(c => c.Id == 0x1549A966);
        if (info is not null)
        {
            // A Title in the Segment Info: it sits in front of the Clusters, so everything after it moves.
            byte[] title = EbmlWriter.Element(0x7BA9, "Deposition, day 2"u8);
            EbmlElement? oldTitle = info.ChildElements.FirstOrDefault(c => c.Id == 0x7BA9);
            if (oldTitle is not null)
            {
                e.Replace(oldTitle, title);
            }
            else
            {
                e.AppendChild(info, title);
            }
        }

        EbmlElement? tags = seg.ChildElements.FirstOrDefault(c => c.Id == TagsId);
        if (tags is not null)
        {
            e.Replace(tags, MatroskaTags("Deposition, day 2"));
        }
        else
        {
            e.AppendChild(seg, MatroskaTags("Deposition, day 2"));
        }
    }

    private static void BasicRiff(ChunkFile cf, ChunkEditor e)
    {
        ChunkNode? existing = cf.Chunks.FirstOrDefault(c => IsRiffMeta(cf.Layout, c));
        if (existing is not null)
        {
            e.Replace(existing, RiffMeta(cf.Layout, "Deposition, day 2"));
        }
        else
        {
            e.AppendChild(null, RiffMeta(cf.Layout, "Deposition, day 2"));
        }
    }

    // ---- edit ----

    private static int Edit(string input, string output)
    {
        Family family = Detect(input);
        Stopwatch sw = Stopwatch.StartNew();
        EditPlan plan;
        using (FileMediaSource src = FileMediaSource.Open(input))
        {
            plan = PlanFor(family, src, strictHeaders: false, edit: true);
        }

        WriteReport report = SafeWriter.SaveAs(plan, input, output, overwrite: true);
        string verify = VerifyCopies(plan, input, output);
        StringBuilder diff = new();
        long at = 0;
        long changedBytes = 0;
        foreach (OutputPiece p in plan.Pieces)
        {
            if (p.Kind != OutputPieceKind.Copy)
            {
                changedBytes += p.Length;
                if (diff.Length < 400)
                {
                    diff.Append($"[{at}+{p.Length}]");
                }
            }

            at += p.Length;
        }

        Console.WriteLine($"{(verify == "ok" ? "OK" : "FAIL")}\t{family}\t{input}\tinputErrors={InputErrors}\tsameLayout={plan.IsSameLayout}\tsrc={plan.SourceLength}\tout={plan.Length}\tchangedBytes={changedBytes}\tcopied={report.BytesCopied}\tcopies={verify}\tchanges={plan.Changes.Count}\t{diff}\t{sw.ElapsedMilliseconds}ms");
        return 0;
    }

    // Every copied piece must read back from the output exactly as it was in the input.
    private static string VerifyCopies(EditPlan plan, string input, string output)
    {
        using FileStream a = File.OpenRead(input);
        using FileStream b = File.OpenRead(output);
        byte[] x = new byte[1 << 20];
        byte[] y = new byte[1 << 20];
        long at = 0;
        foreach (OutputPiece p in plan.Pieces)
        {
            if (p.Kind == OutputPieceKind.Copy)
            {
                a.Position = p.SourceRange.Offset;
                b.Position = at;
                long left = p.Length;
                while (left > 0)
                {
                    int n = (int)Math.Min(left, x.Length);
                    a.ReadExactly(x, 0, n);
                    b.ReadExactly(y, 0, n);
                    if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n)))
                    {
                        return $"MISMATCH at output {at}";
                    }

                    left -= n;
                    at += n;
                }
            }
            else
            {
                at += p.Length;
            }
        }

        return b.Length == at ? "ok" : "LENGTH";
    }

    // ---- hash ----

    private static int Hash(string path)
    {
        using FileMediaSource src = FileMediaSource.Open(path);
        using MediaFile mf = MediaFile.Open(src);
        IDemuxer d = mf.Demuxer ?? throw new UnsupportedMediaException("No synchronous demuxer.");
        Dictionary<int, IncrementalHash> hashes = [];
        Dictionary<int, long> counts = [];
        while (d.TryReadPacket(out PacketView v))
        {
            int t = v.Info.TrackIndex;
            if (!hashes.TryGetValue(t, out IncrementalHash? h))
            {
                hashes[t] = h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }

            h.AppendData(v.Data);
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }

        foreach (int t in hashes.Keys.Order())
        {
            Console.WriteLine($"{t}\t{counts[t]}\t{Convert.ToHexString(hashes[t].GetHashAndReset())}");
        }

        return 0;
    }

    // ---- in place ----

    private static int InPlace(string input, string workDir)
    {
        Directory.CreateDirectory(workDir);
        Family family = Detect(input);
        string padded = Path.Combine(workDir, "padded" + Path.GetExtension(input));
        string name = Path.GetFileName(input);

        // 1. A new file with 2 KiB of padding, then the metadata unit written into that padding.
        string padOnly = Path.Combine(workDir, "pad-only" + Path.GetExtension(input));
        using (FileMediaSource src = FileMediaSource.Open(input))
        {
            SafeWriter.SaveAs(PlanPadded(family, src, null), input, padOnly, overwrite: true);
        }

        using (FileMediaSource src = FileMediaSource.Open(padOnly))
        {
            SafeWriter.SaveAs(PlanPadded(family, src, "first title"), padOnly, padded, overwrite: true);
        }

        string hash0 = HashLines(input);
        string hash1 = HashLines(padded);

        // 2. A longer title that fits the padding: planned as the same layout and applied in place.
        byte[] before = File.ReadAllBytes(padded);
        EditPlan fit;
        using (FileMediaSource src = FileMediaSource.Open(padded))
        {
            fit = PlanLast(family, src, new string('t', 300));
        }

        string fitResult;
        if (!fit.IsSameLayout)
        {
            fitResult = "NOT-SAME-LAYOUT";
        }
        else
        {
            WriteReport r = SafeWriter.SaveInPlace(fit, padded);
            byte[] after = File.ReadAllBytes(padded);
            (long lo, long hi) = DiffExtent(before, after);
            bool inside = r.Changes.Any(c => lo >= c.Output.Offset && hi < c.Output.End) || lo < 0;
            fitResult = $"inplace ok, differs [{lo},{hi}] within change={inside}, padUsed={r.PaddingUsed}, copied={r.BytesCopied}, length {before.Length}->{after.Length}";
            if (before.Length != after.Length || !inside)
            {
                fitResult += " FAIL";
            }
        }

        string hash2 = HashLines(padded);

        // 3. A title far bigger than the padding: refused, file untouched.
        byte[] untouched = File.ReadAllBytes(padded);
        EditPlan big;
        using (FileMediaSource src = FileMediaSource.Open(padded))
        {
            big = PlanLast(family, src, new string('x', 50_000));
        }

        string refusal;
        try
        {
            SafeWriter.SaveInPlace(big, padded);
            refusal = "NOT-REFUSED FAIL";
        }
        catch (PaddingRequiredException e)
        {
            refusal = File.ReadAllBytes(padded).AsSpan().SequenceEqual(untouched) ? $"refused required={e.Required} available={e.Available}, file untouched" : "refused but FILE CHANGED FAIL";
        }

        bool ok = hash0 == hash1 && hash1 == hash2 && !fitResult.Contains("FAIL") && fitResult != "NOT-SAME-LAYOUT" && !refusal.Contains("FAIL");
        Console.WriteLine($"{(ok ? "OK" : "FAIL")}\t{family}\t{name}\tpayloadHash={(hash0 == hash1 && hash1 == hash2 ? "unchanged" : "CHANGED")}\t{fitResult}\t{refusal}");
        return 0;
    }

    private static string HashLines(string path)
    {
        StringWriter w = new();
        TextWriter old = Console.Out;
        Console.SetOut(w);
        try
        {
            Hash(path);
        }
        catch (Exception e) when (e is MediaException or IOException)
        {
            w.WriteLine("hash-error " + e.Message);
        }
        finally
        {
            Console.SetOut(old);
        }

        return w.ToString();
    }

    private static (long Lo, long Hi) DiffExtent(byte[] a, byte[] b)
    {
        int lo = 0;
        while (lo < a.Length && a[lo] == b[lo])
        {
            lo++;
        }

        if (lo == a.Length)
        {
            return (-1, -1);
        }

        int hi = a.Length - 1;
        while (a[hi] == b[hi])
        {
            hi--;
        }

        return (lo, hi);
    }

    private static EditPlan PlanPadded(Family family, FileMediaSource src, string? title)
    {
        switch (family)
        {
            case Family.Iso:
                {
                    BoxTree tree = new(src);
                    BoxEditor e = new(tree, src);
                    BoxNode moov = tree.Find(BoxTypes.Moov)!;
                    if (title is null)
                    {
                        e.AppendChild(moov, BoxBuilder.CreateFree(2048));
                    }
                    else
                    {
                        e.AppendChild(moov, IlstUdta(title));
                    }

                    return e.Build();
                }

            case Family.Matroska:
                {
                    EbmlDocument doc = EbmlDocument.Open(src);
                    EbmlEditor e = new(doc, src);
                    e.AppendChild(doc.Segment, title is null ? EbmlWriter.Void(2048) : MatroskaTags(title));
                    return e.Build();
                }

            default:
                {
                    ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
                    ChunkEditor e = new(cf, src);
                    e.AppendChild(null, title is null ? ChunkBuilder.CreatePadding(cf.Layout, 2048) : RiffMeta(cf.Layout, title));
                    return e.Build();
                }
        }
    }

    private static EditPlan PlanLast(Family family, FileMediaSource src, string title)
    {
        switch (family)
        {
            case Family.Iso:
                {
                    BoxTree tree = new(src);
                    BoxEditor e = new(tree, src);
                    BoxNode moov = tree.Find(BoxTypes.Moov)!;
                    e.Replace(moov.Children.Last(c => c.Type == BoxTypes.Udta && Contains(src, c.Offset, c.Header.End - c.Offset)), IlstUdta(title));
                    return e.Build();
                }

            case Family.Matroska:
                {
                    EbmlDocument doc = EbmlDocument.Open(src);
                    EbmlEditor e = new(doc, src);
                    e.Replace(doc.Segment!.ChildElements.Last(c => c.Id == TagsId && Contains(src, c.Offset, c.Range.Length)), MatroskaTags(title));
                    return e.Build();
                }

            default:
                {
                    ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
                    ChunkEditor e = new(cf, src);
                    e.Replace(cf.Chunks.Last(c => IsRiffMeta(cf.Layout, c) && Contains(src, c.Offset, c.NextOffset - c.Offset)), RiffMeta(cf.Layout, title));
                    return e.Build();
                }
        }
    }

    // The metadata unit this tool added in step 1 (an earlier padding box may have taken it in, so it is not necessarily the last one).
    private static bool Contains(FileMediaSource src, long offset, long length)
    {
        byte[] bytes = new byte[length];
        src.ReadExactly(offset, bytes);
        return bytes.AsSpan().IndexOf("first title"u8) >= 0 || bytes.AsSpan().IndexOf("tttttttttt"u8) >= 0;
    }

    // ---- save (atomic) ----

    private static int Save(string path)
    {
        Family family = Detect(path);
        EditPlan plan;
        using (FileMediaSource src = FileMediaSource.Open(path))
        {
            plan = PlanFor(family, src, strictHeaders: false, edit: true);
        }

        WriteReport r = SafeWriter.Save(plan, path);
        Console.WriteLine($"saved\t{path}\t{r.SourceLength}->{r.OutputLength}\tcopied={r.BytesCopied}\twritten={r.BytesWritten}");
        return 0;
    }
}
