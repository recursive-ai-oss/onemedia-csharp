using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>tags</c> command: shows a file's tags, or changes them through <see cref="TagWriter"/>.</summary>
internal static class TagsCommand
{
    public static int Run(string[] args)
    {
        string path = args[0];
        TagEdits edits = new();
        List<string> systems = [];
        string? output = null;
        bool replace = false;
        bool inPlace = false;
        bool check = false;
        string? captureTime = null;
        bool? headerLocal = null;
        string coverType = nameof(PictureType.FrontCover);
        string? cover = null;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--set" when i + 1 < args.Length:
                        string[] kv = args[++i].Split('=', 2);
                        if (kv.Length != 2 || !TryField(kv[0], out CommonField f))
                        {
                            Console.Error.WriteLine("--set expects field=value; fields: " + string.Join(", ", Enum.GetNames<CommonField>()));
                            return 2;
                        }

                        edits.Set(f, kv[1]);
                        break;
                    case "--remove" when i + 1 < args.Length:
                        if (!TryField(args[++i], out CommonField r))
                        {
                            Console.Error.WriteLine("unknown field " + args[i]);
                            return 2;
                        }

                        edits.Remove(r);
                        break;
                    case "--cover" when i + 1 < args.Length:
                        cover = args[++i];
                        break;
                    case "--cover-type" when i + 1 < args.Length:
                        coverType = args[++i];
                        break;
                    case "--remove-covers":
                        edits.RemovePictures();
                        break;
                    case "--capture-time" when i + 1 < args.Length:
                        captureTime = args[++i];
                        break;
                    case "--header-times" when i + 1 < args.Length:
                        string mode = args[++i];
                        if (mode is not ("utc" or "local"))
                        {
                            Console.Error.WriteLine("--header-times expects utc or local");
                            return 2;
                        }

                        headerLocal = mode == "local";
                        break;
                    case "--system" when i + 1 < args.Length:
                        systems.Add(args[++i]);
                        break;
                    case "--out" when i + 1 < args.Length:
                        output = args[++i];
                        break;
                    case "--replace":
                        replace = true;
                        break;
                    case "--in-place":
                        inPlace = true;
                        break;
                    case "--check":
                        check = true;
                        break;
                    default:
                        Console.Error.WriteLine("unknown option " + args[i]);
                        return 2;
                }
            }

            if (cover is not null)
            {
                byte[] image = File.ReadAllBytes(cover);
                string mime = Path.GetExtension(cover).ToLowerInvariant() switch { ".png" => "image/png", ".gif" => "image/gif", ".bmp" => "image/bmp", ".webp" => "image/webp", _ => "image/jpeg" };
                edits.SetPicture(image, mime, Enum.TryParse(coverType, true, out PictureType t) ? t : PictureType.FrontCover);
            }

            if (check)
            {
                IReadOnlyList<TagResidue> residue = TagWriter.CheckResidue(path);
                foreach (TagResidue r in residue)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r.System}: {r.Kind} at {r.Offset}, {r.Length} byte(s): {r.Detail}"));
                }

                Console.WriteLine(residue.Count == 0 ? "clean: no leftover removed or stale values found" : "leftovers found: save the file to zero them: onemedia tags <file> --replace");
                return residue.Count == 0 ? 0 : 1;
            }

            if (captureTime is not null)
            {
                if (!edits.IsEmpty)
                {
                    Console.Error.WriteLine("--capture-time cannot be combined with --set, --remove or cover options; run them separately");
                    return 2;
                }

                return SetCaptureTime(path, captureTime, headerLocal, output, replace, inPlace);
            }

            if (edits.IsEmpty && output is null && !replace && !inPlace)
            {
                return Show(path);
            }

            TagWriteOptions options = new() { Systems = systems.Count > 0 ? systems : null };
            TagWriteReport report;
            if (output is not null)
            {
                report = TagWriter.SaveAs(path, output, edits, options);
            }
            else if (replace)
            {
                report = TagWriter.Save(path, edits, options);
            }
            else if (inPlace)
            {
                report = TagWriter.SaveInPlace(path, edits, options);
            }
            else
            {
                report = TagWriter.Plan(path, edits, options).Report;
                Console.WriteLine("plan only (no --out, --replace or --in-place): nothing was written");
            }

            Print(report);
            return 0;
        }
        catch (PaddingRequiredException e)
        {
            Console.Error.WriteLine(e.ShortfallIn is null
                ? $"the edit needs {e.Required} byte(s) and the file has {e.Available} of padding; use --out or --replace"
                : $"{e.Message} Use --out or --replace, or --system to limit the edit.");
            return 1;
        }
        catch (MediaException e)
        {
            Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    // An ISO 8601 date-time with an offset or Z; without one the time is taken as UTC.
    private static int SetCaptureTime(string path, string text, bool? headerLocal, string? output, bool replace, bool inPlace)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when))
        {
            Console.Error.WriteLine("--capture-time expects an ISO 8601 date-time such as 2024-05-01T12:30:00+02:00");
            return 2;
        }

        CaptureTimeOptions options = new() { HeaderTimesAreLocal = headerLocal };
        CaptureTimeReport report;
        if (output is not null)
        {
            report = CaptureTimeWriter.SaveAs(path, output, when, options);
        }
        else if (replace)
        {
            report = CaptureTimeWriter.Save(path, when, options);
        }
        else if (inPlace)
        {
            report = CaptureTimeWriter.SaveInPlace(path, when, options);
        }
        else
        {
            report = CaptureTimeWriter.Plan(path, when, options).Report;
            Console.WriteLine("plan only (no --out, --replace or --in-place): nothing was written");
        }

        Console.WriteLine("header times: " + report.HeaderTimesBasis);
        foreach (DateFieldWrite w in report.Written)
        {
            Console.WriteLine($"written  {w.System} {w.Field}: {w.OldValue ?? "(none)"} -> {w.NewValue}   [{w.Semantics}]");
        }

        foreach (DateFieldLeft l in report.Left)
        {
            Console.WriteLine($"left     {l.System} {l.Field}: {l.Value ?? "(none)"}   [{l.Reason}]");
        }

        foreach (DateDisagreement d in report.Disagreements)
        {
            Console.WriteLine($"differs  {d.Source}: {d.RawValue}   [{d.Note}]");
        }

        if (report.Write is { } w2)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{w2.Mode}: {w2.BytesWritten} byte(s) written, {w2.BytesCopied} copied"));
        }

        return 0;
    }

    private static bool TryField(string name, out CommonField field) =>
        Enum.TryParse(name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal), true, out field);

    private static int Show(string path)
    {
        using MediaFile media = Media.Open(path);
        foreach (CommonField f in Enum.GetValues<CommonField>())
        {
            foreach (CommonValue v in media.Tags.Values(f))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f}: {v.Text}   [{v.Source.System}:{v.Source.Key}]"));
            }
        }

        foreach (TagPicture p in media.Tags.Pictures)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Picture: {p.Type} {p.MimeType} {p.Length} bytes   [{p.Source.System}:{p.Source.Key}]"));
        }

        return 0;
    }

    private static void Print(TagWriteReport report)
    {
        foreach (SystemWriteReport s in report.Systems)
        {
            Console.WriteLine($"{s.System}{(s.Created ? " (created)" : "")}: written [{string.Join(", ", s.Written)}] removed [{string.Join(", ", s.Removed)}]");
            foreach ((CommonField field, string reason) in s.NotRepresentable)
            {
                Console.WriteLine($"  not written: {field}: {reason}");
            }

            foreach (string c in s.Conversions)
            {
                Console.WriteLine("  converted: " + c);
            }
        }

        foreach (string n in report.Notes)
        {
            Console.WriteLine("note: " + n);
        }

        foreach (string warning in report.Warnings)
        {
            Console.WriteLine("warning: " + warning);
        }

        if (report.Write is { } w)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{w.Mode}: {w.BytesWritten} byte(s) written, {w.BytesCopied} copied"));
        }
    }
}
