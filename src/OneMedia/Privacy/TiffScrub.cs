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

        long end = fileOffset + data.Length;
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

                    sink.Patches.Write(e.EntryRange.Offset, [0xFF, 0xFF]);
                }
            }
        }

        return true;
    }
}
