using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Strips an embedded EXIF block (TIFF 6.0 structure as used by Exif 2.32 and CIPA DC-008): the GPS IFD is emptied (its entry count becomes 0 and every
/// byte of it and its out-of-line values becomes zero) and the serial-number, owner, image-unique-id and maker-note entries get their values zeroed and
/// their tag ids changed to 0xFFFF, which no standard assigns, so a reader no longer reports them. Offsets and sizes of everything else stay.
/// </summary>
internal static class TiffScrub
{
    private const int MaxIfds = 8;
    private const int MaxEntries = 4096;

    // Exif 2.32 Table 4: 0x8825 GPSInfo IFD pointer, 0x8769 Exif IFD pointer, 0xA431 BodySerialNumber, 0xA435 LensSerialNumber, 0xA430 CameraOwnerName,
    // 0xA420 ImageUniqueID, 0x927C MakerNote; DNG 1.4: 0xC62F CameraSerialNumber.
    private static readonly (ushort Tag, string Name)[] DeviceTags =
    [
        (0xA431, "BodySerialNumber"), (0xA435, "LensSerialNumber"), (0xA430, "CameraOwnerName"), (0xA420, "ImageUniqueID"),
        (0xC62F, "CameraSerialNumber"), (0x927C, "MakerNote"),
    ];

    /// <summary>Scrubs a TIFF block (optionally preceded by the six bytes <c>Exif\0\0</c>).</summary>
    /// <param name="data">The block.</param>
    /// <param name="fileOffset">Where <paramref name="data"/> starts in the file.</param>
    /// <param name="system">The report's system name.</param>
    /// <param name="trackId">The report's track id.</param>
    /// <param name="cats">The categories to strip.</param>
    /// <param name="sink">Receives the patches.</param>
    /// <returns>False when the bytes are not a TIFF structure (nothing was changed).</returns>
    public static bool Scrub(ReadOnlySpan<byte> data, long fileOffset, string system, long trackId, StripCategories cats, StripSink sink)
    {
        int start = data.Length >= 6 && data[0] == (byte)'E' && data[1] == (byte)'x' && data[2] == (byte)'i' && data[3] == (byte)'f' && data[4] == 0 && data[5] == 0 ? 6 : 0;
        ReadOnlySpan<byte> t = data[start..];
        if (t.Length < 8)
        {
            return false;
        }

        bool le;
        if (t[0] == (byte)'I' && t[1] == (byte)'I')
        {
            le = true;
        }
        else if (t[0] == (byte)'M' && t[1] == (byte)'M')
        {
            le = false;
        }
        else
        {
            return false;
        }

        if (U16(t, 2, le) != 42)
        {
            return false;
        }

        HashSet<long> seen = [];
        Walk(t, U32(t, 4, le), le, fileOffset + start, system, trackId, cats, sink, seen, 0);
        return true;
    }

    private static void Walk(ReadOnlySpan<byte> t, long ifd, bool le, long baseOffset, string system, long trackId, StripCategories cats, StripSink sink, HashSet<long> seen, int depth)
    {
        while (ifd > 0 && ifd + 2 <= t.Length && seen.Count < MaxIfds && seen.Add(ifd))
        {
            int n = U16(t, (int)ifd, le);
            if (n > MaxEntries || ifd + 2 + (12L * n) > t.Length)
            {
                return;
            }

            for (int i = 0; i < n; i++)
            {
                int e = (int)ifd + 2 + (12 * i);
                ushort tag = (ushort)U16(t, e, le);
                if (tag == 0x8825 && (cats & StripCategories.Location) != 0)
                {
                    ScrubGps(t, U32(t, e + 8, le), le, baseOffset, system, trackId, sink);
                }
                else if (tag == 0x8769 && depth < 3)
                {
                    Walk(t, U32(t, e + 8, le), le, baseOffset, system, trackId, cats, sink, seen, depth + 1);
                }
                else if ((cats & StripCategories.DeviceIds) != 0)
                {
                    foreach ((ushort dt, string name) in DeviceTags)
                    {
                        if (dt == tag)
                        {
                            ScrubValue(t, e, le, baseOffset, system, name, trackId, sink);
                        }
                    }
                }
            }

            long next = ifd + 2 + (12L * n);
            ifd = next + 4 <= t.Length ? U32(t, (int)next, le) : 0;
        }
    }

    private static void ScrubGps(ReadOnlySpan<byte> t, long ifd, bool le, long baseOffset, string system, long trackId, StripSink sink)
    {
        if (ifd <= 0 || ifd + 2 > t.Length)
        {
            return;
        }

        int n = U16(t, (int)ifd, le);
        if (n > MaxEntries || ifd + 2 + (12L * n) > t.Length)
        {
            return;
        }

        for (int i = 0; i < n; i++)
        {
            int e = (int)ifd + 2 + (12 * i);
            long size = ValueSize(t, e, le);
            if (size > 4)
            {
                long off = U32(t, e + 8, le);
                if (off + size <= t.Length)
                {
                    sink.Zero(StripCategories.Location, system, "EXIF GPS value", trackId, baseOffset + off, size);
                }
            }
        }

        // The count, the entries and the next-IFD pointer: an IFD with zero entries.
        sink.Zero(StripCategories.Location, system, "EXIF GPS IFD", trackId, baseOffset + ifd, Math.Min(2 + (12L * n) + 4, t.Length - ifd));
    }

    private static void ScrubValue(ReadOnlySpan<byte> t, int e, bool le, long baseOffset, string system, string name, long trackId, StripSink sink)
    {
        long size = ValueSize(t, e, le);
        if (size > 4)
        {
            long off = U32(t, e + 8, le);
            if (off + size <= t.Length)
            {
                sink.Zero(StripCategories.DeviceIds, system, "EXIF " + name, trackId, baseOffset + off, size);
            }
        }
        else
        {
            sink.Zero(StripCategories.DeviceIds, system, "EXIF " + name, trackId, baseOffset + e + 8, 4);
        }

        sink.Patches.Write(baseOffset + e, [0xFF, 0xFF]);
    }

    private static readonly int[] TypeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];

    private static long ValueSize(ReadOnlySpan<byte> t, int e, bool le)
    {
        int type = U16(t, e + 2, le);
        long count = U32(t, e + 4, le);
        return type is >= 1 and <= 12 ? count * TypeSize[type] : 0;
    }

    private static int U16(ReadOnlySpan<byte> t, int at, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(t[at..]) : BinaryPrimitives.ReadUInt16BigEndian(t[at..]);

    private static long U32(ReadOnlySpan<byte> t, int at, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(t[at..]) : BinaryPrimitives.ReadUInt32BigEndian(t[at..]);
}
