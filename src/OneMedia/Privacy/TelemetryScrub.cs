using System.Buffers.Binary;
using System.Linq;
using RecursiveAI.OneMedia.Metadata.Telemetry.Mebx;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>Collects what the strip neutralises: the byte patches, and one aggregated report entry per (category, system, name, track).</summary>
internal sealed class StripSink
{
    private sealed class Agg
    {
        public StripCategories Category;
        public string System = "";
        public string Name = "";
        public long TrackId;
        public long First = long.MaxValue;
        public long Last;
        public int Count;
        public long Bytes;
        public StripAction Action;
    }

    private readonly Dictionary<(StripCategories, string, string, long, StripAction), Agg> _byKey = [];
    private readonly List<Agg> _order = [];
    private readonly List<StripLeftover> _left = [];
    private readonly List<string> _notes = [];

    public StripSink(PatchSet patches) => Patches = patches;

    public PatchSet Patches { get; }

    public IReadOnlyList<StripLeftover> Leftovers => _left;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Overwrites a range with zeros and records it.</summary>
    public void Zero(StripCategories cat, string system, string name, long trackId, long offset, long length, StripAction action = StripAction.Zeroed)
    {
        if (length <= 0)
        {
            return;
        }

        Patches.Zero(offset, length);
        Note(cat, system, name, trackId, offset, length, action);
    }

    /// <summary>Records a change another path makes (a rewritten box).</summary>
    public void Note(StripCategories cat, string system, string name, long trackId, long offset, long length, StripAction action, long? bytes = null)
    {
        (StripCategories, string, string, long, StripAction) key = (cat, system, name, trackId, action);
        if (!_byKey.TryGetValue(key, out Agg? a))
        {
            a = new Agg { Category = cat, System = system, Name = name, TrackId = trackId, Action = action };
            _byKey[key] = a;
            _order.Add(a);
        }

        a.First = Math.Min(a.First, offset);
        a.Last = Math.Max(a.Last, offset + length);
        a.Count++;
        a.Bytes += bytes ?? length;
    }

    public void Leftover(StripCategories cat, string system, string name, long trackId, ByteRange range, string reason) =>
        _left.Add(new StripLeftover(cat, system, name, trackId, range, reason));

    public void AddNote(string note) => _notes.Add(note);

    public IReadOnlyList<StripEntry> Entries() =>
        [.. _order.Select(a => new StripEntry(a.Category, a.System, a.Name, a.TrackId, new ByteRange(a.First, a.Last - a.First), a.Count, a.Bytes, a.Action))];
}

/// <summary>
/// Finds the location and device-identifier fields inside timed metadata samples and records them as zero runs. Each scrubber keeps every
/// header and length field as it was, so the sample still parses; only the values go.
/// </summary>
internal static class TelemetryScrub
{
    private const int MaxDepth = 8;
    private static readonly byte[] EmptyKey = "EMPT"u8.ToArray();
    private static readonly byte[] UnknownCammType = [0xFF, 0xFF];
    private static readonly byte[] PlaceholderId = [0, 0, 0, 0];

    /// <summary>
    /// GoPro GPMF (GoPro's GPMF format documentation): a stream of KLV items, each a four-character key, a type byte, a size byte, a 16-bit big-endian
    /// repeat count and size*repeat data bytes padded to four. Type 0 is a nested KLV stream. The data of a listed key is zeroed; the 8-byte item header stays.
    /// </summary>
    public static void Gpmf(ReadOnlySpan<byte> data, long fileOffset, string system, long trackId, StripCategories cats, StripSink sink, int depth = 0)
    {
        int pos = 0;
        while (data.Length - pos >= 8)
        {
            string key = System.Text.Encoding.Latin1.GetString(data.Slice(pos, 4));
            byte type = data[pos + 4];
            long length = (long)data[pos + 5] * BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 6)..]);
            int body = pos + 8;
            int avail = (int)Math.Min(length, data.Length - body);
            if (type == 0)
            {
                if (depth < MaxDepth)
                {
                    Gpmf(data.Slice(body, avail), fileOffset + body, system, trackId, cats, sink, depth + 1);
                }
            }
            else
            {
                StripCategories c = StripRules.ClassifyGpmf(key);
                if ((c & cats) != 0 && avail > 0 && fileOffset >= 0)
                {
                    sink.Zero(c, system, key, trackId, fileOffset + body, avail);
                    // The key becomes EMPT, a key GoPro's documentation lists as an empty item, so readers no longer report the stream or field.
                    sink.Patches.Write(fileOffset + pos, EmptyKey);
                }
            }

            long step = 8 + ((length + 3) & ~3L);
            if (step > data.Length - pos)
            {
                break;
            }

            pos += (int)step;
        }
    }

    /// <summary>
    /// Google camera motion metadata (CAMM specification): a 16-bit reserved field, a little-endian 16-bit packet type, then the packet. Types 4
    /// (local position), 5 (latitude, longitude, altitude) and 6 (GPS with time, fix type, accuracy and velocity) are zeroed after the 4-byte header.
    /// Zero as a fix type means "no fix" in type 6.
    /// </summary>
    public static void Camm(ReadOnlySpan<byte> data, long fileOffset, long trackId, StripCategories cats, StripSink sink)
    {
        if ((cats & StripCategories.Location) == 0 || data.Length <= 4 || fileOffset < 0)
        {
            return;
        }

        int type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (type is 4 or 5 or 6)
        {
            sink.Zero(StripCategories.Location, "camm", "camm type " + type, trackId, fileOffset + 4, data.Length - 4);
            // The packet type becomes 0xFFFF, which the CAMM specification does not assign, so a reader skips the packet.
            sink.Patches.Write(fileOffset + 2, UnknownCammType);
        }
    }

    /// <summary>
    /// QuickTime timed metadata (<c>mebx</c>): a sample is a run of atoms typed by local key id; the sample entry's key table names each id. The value of
    /// an atom whose key is a location, a detected face or a device identifier is zeroed, nested atoms included.
    /// </summary>
    public static void Mebx(ReadOnlySpan<byte> sample, long fileOffset, long trackId, MebxKeyTable? table, StripCategories cats, StripSink sink, int depth = 0)
    {
        int pos = 0;
        while (MebxKeyTable.TryBox(sample, pos, out uint id, out int payload, out int end))
        {
            pos = end;
            if (id == 0 || id == 0xFFFFFFFF)
            {
                continue;
            }

            MebxKey? key = table?.Find(id);
            StripCategories c = key is null ? StripCategories.None : StripRules.ClassifyWords(key.Key);
            if ((c & cats) != 0 && fileOffset >= 0)
            {
                sink.Zero(c & cats, "mebx", key!.Key, trackId, fileOffset + payload, end - payload);
                // Local id 0 marks a placeholder atom, which readers of the sample skip.
                sink.Patches.Write(fileOffset + payload - 4, PlaceholderId);
            }
            else if (key is null && depth < MaxDepth && end - payload >= 8)
            {
                // A grouping atom (a detected face holds nested atoms typed by sub-key ids). Same test the decoder uses.
                Mebx(sample[payload..end], fileOffset >= 0 ? fileOffset + payload : -1, trackId, table, cats, sink, depth + 1);
            }
        }
    }

    /// <summary>
    /// Zeroes every occurrence of each secret in a sample: the settings track of GoPro cameras ("fdsc") repeats the lens serial number, the camera serial
    /// number and its hash and the ISO 6709 position as unlabelled blobs, and its layout is not documented, so the strip finds them by value (the values
    /// were read from the labelled places that were removed).
    /// </summary>
    public static void BySecrets(ReadOnlySpan<byte> sample, long fileOffset, string system, long trackId, StripCategories cats, IReadOnlyList<Secret> secrets, StripSink sink)
    {
        if (fileOffset < 0)
        {
            return;
        }

        foreach (Secret s in secrets)
        {
            if ((s.Category & cats) == 0 || s.Bytes.Length < 6)
            {
                continue;
            }

            int from = 0;
            while (from <= sample.Length - s.Bytes.Length)
            {
                int i = sample[from..].IndexOf(s.Bytes);
                if (i < 0)
                {
                    break;
                }

                sink.Zero(s.Category, system, s.Name, trackId, fileOffset + from + i, s.Bytes.Length);
                from += i + s.Bytes.Length;
            }
        }
    }
}

/// <summary>A value that was read from a removed field, to be found again elsewhere.</summary>
/// <param name="Category">The category of the field it came from.</param>
/// <param name="Name">The field's name, for the report.</param>
/// <param name="Bytes">The value as stored.</param>
internal sealed record Secret(StripCategories Category, string Name, byte[] Bytes);
