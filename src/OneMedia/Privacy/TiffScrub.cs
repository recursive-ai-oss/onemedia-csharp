using RecursiveAI.OneMedia.Exif;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Strips an embedded Exif block (Exif 2.32, TIFF 6.0), found with <see cref="ExifBlock"/>: the GPS directory is emptied (its entry count, entries and
/// next-directory link become zero, and so does every value it holds) and the serial-number, owner, image-unique-id and maker-note entries get their
/// values zeroed and their tag ids changed to 0xFFFF, which no standard assigns, so a reader no longer reports them. Nothing else moves.
/// </summary>
internal static class TiffScrub
{
    // Exif 2.32 Table 4: 0xA431 BodySerialNumber, 0xA435 LensSerialNumber, 0xA430 CameraOwnerName, 0xA420 ImageUniqueID, 0x927C MakerNote;
    // DNG 1.4: 0xC62F CameraSerialNumber.
    private static readonly (ushort Tag, string Name)[] DeviceTags =
    [
        (0xA431, "BodySerialNumber"), (0xA435, "LensSerialNumber"), (0xA430, "CameraOwnerName"), (0xA420, "ImageUniqueID"),
        (0xC62F, "CameraSerialNumber"), (0x927C, "MakerNote"),
    ];

    /// <summary>Scrubs a block (optionally preceded by a few wrapper bytes such as <c>Exif\0\0</c>).</summary>
    /// <param name="data">The block.</param>
    /// <param name="fileOffset">Where <paramref name="data"/> starts in the file; negative when unknown (nothing can be changed then).</param>
    /// <param name="system">The report's system name.</param>
    /// <param name="trackId">The report's track id.</param>
    /// <param name="cats">The categories to strip.</param>
    /// <param name="sink">Receives the patches.</param>
    /// <param name="limits">Limits for parsing.</param>
    /// <returns>False when the bytes hold no Exif block (nothing was changed).</returns>
    public static bool Scrub(ReadOnlyMemory<byte> data, long fileOffset, string system, long trackId, StripCategories cats, StripSink sink, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        if (fileOffset < 0 || !ExifBlock.TryParse(data, fileOffset, limits, new DefectLog(limits), out ExifBlock block))
        {
            return false;
        }

        ScrubBlock(block, fileOffset + data.Length, system, trackId, cats, sink);
        return true;
    }

    /// <summary>
    /// Scrubs the <c>strd</c> chunk of a Fujifilm AVI video stream: the text <c>AVIF</c>, four bytes, then a little-endian Exif directory without a TIFF header whose
    /// offsets count from the directory's first byte. Its Exif directory holds a maker note with the camera's internal serial number (observed with ExifTool in a FinePix movie).
    /// The same rules as <see cref="Scrub"/> apply, so the maker note is zeroed whole and nothing moves.
    /// </summary>
    /// <param name="chunk">The chunk's data (without the chunk header).</param>
    /// <param name="fileOffset">Where <paramref name="chunk"/> starts in the file.</param>
    /// <param name="system">The report's system name.</param>
    /// <param name="trackId">The report's track id.</param>
    /// <param name="cats">The categories to strip.</param>
    /// <param name="sink">Receives the patches.</param>
    /// <param name="limits">Limits for parsing.</param>
    /// <returns>False when the chunk does not have the layout (nothing was changed).</returns>
    public static bool ScrubFujifilmAviStrd(ReadOnlyMemory<byte> chunk, long fileOffset, string system, long trackId, StripCategories cats, StripSink sink, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        if (fileOffset < 0 || chunk.Length < 10 || !chunk.Span.StartsWith("AVIF"u8) || chunk.Length > limits.MaxTagBytes)
        {
            return false;
        }

        ExifBlock block = ExifBlock.ParseHeaderless(chunk[8..], fileOffset + 8, new DefectLog(limits));
        ScrubBlock(block, fileOffset + chunk.Length, system, trackId, cats, sink);
        return true;
    }

    private static void ScrubBlock(ExifBlock block, long end, string system, long trackId, StripCategories cats, StripSink sink)
    {
        if ((cats & StripCategories.Location) != 0)
        {
            long first = long.MaxValue, last = 0;
            foreach (ExifEntry e in block.Entries)
            {
                if (e.Ifd != "GPS" || e.EntryRange.IsNone)
                {
                    continue;
                }

                if (!e.ValueRange.IsNone)
                {
                    sink.Zero(StripCategories.Location, system, "EXIF GPS value", trackId, e.ValueRange.Offset, e.ValueRange.Length);
                }

                first = Math.Min(first, e.EntryRange.Offset);
                last = Math.Max(last, e.EntryRange.End);
            }

            if (first != long.MaxValue)
            {
                // The count (2 bytes) before the first entry, the entries, and the 4-byte link after them: a directory with zero entries.
                long from = first - 2;
                sink.Zero(StripCategories.Location, system, "EXIF GPS IFD", trackId, from, Math.Min(last + 4, end) - from);
            }
        }

        if ((cats & StripCategories.DeviceIds) != 0)
        {
            foreach (ExifEntry e in block.Entries)
            {
                foreach ((ushort tag, string name) in DeviceTags)
                {
                    if (tag != e.Tag || e.EntryRange.IsNone)
                    {
                        continue;
                    }

                    if (!e.ValueRange.IsNone)
                    {
                        sink.Zero(StripCategories.DeviceIds, system, "EXIF " + name, trackId, e.ValueRange.Offset, e.ValueRange.Length);
                    }
                    else if (e.Count > 0)
                    {
                        sink.Leftover(StripCategories.DeviceIds, system, "EXIF " + name, trackId, e.EntryRange, "The value lies outside the block that was read, so it was not zeroed.");
                    }

                    sink.Patches.Write(e.EntryRange.Offset, [0xFF, 0xFF]);
                }
            }
        }
    }
}
