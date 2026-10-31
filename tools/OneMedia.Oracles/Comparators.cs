using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Compares audio tracks with OneAudio's own packet readers: <c>Mp4PacketSource</c> for MP4/3GP and
/// <c>OggPacketSource</c> for Ogg files remuxed from Matroska audio.
/// </summary>
internal static class OneAudioCompare
{
    /// <summary>Our audio tracks against Mp4PacketSource's, packet by packet (size, payload hash, timestamp, duration).</summary>
    public static FileReport CompareMp4(string file, OurResult ours, Exceptions exceptions)
    {
        FileReport report = new() { File = file, Format = ours.Format, Status = ours.Status, Message = ours.Message };
        if (ours.Status != "ok")
        {
            return report;
        }

        List<MediaTrack> audio = [.. ours.Tracks.Where(t => t.Kind == TrackKind.Audio)];
        List<PacketRecord> theirs = [];
        byte[] digest = new byte[SHA256.HashSizeInBytes];
        using FileStream fs = File.OpenRead(file);
        using OneAudio.Mp4.Mp4PacketSource source = OneAudio.Mp4.Mp4PacketSource.Open(fs, leaveOpen: true);
        for (int k = 0; k < source.Tracks.Count && k < audio.Count; k++)
        {
            if (source.Tracks[k].Codec is null)
            {
                continue;
            }

            source.SelectTrack(k);
            MediaTrack track = audio[k];
            int scale = source.TimeScale;
            List<PacketRecord> mine = [.. ours.Packets.Where(p => p.Track == track.Index)];
            int i = 0;
            while (source.TryReadPacket(out OneAudio.PacketView v))
            {
                SHA256.HashData(v.Data, digest);
                PacketRecord m = i < mine.Count ? mine[i] : default;
                // OneAudio reports decode time and duration in the media timescale; it has no position or sync flag.
                // OneAudio reports media times; our records are placed on ffprobe's edited timeline, so the same shift is applied here.
                MediaTime ts = new(v.Timestamp, scale);
                if (ours.Shifts.Length > track.Index)
                {
                    ts = OurReader.Shifted(ts, ours.Shifts[track.Index]);
                }

                theirs.Add(new PacketRecord(track.Index, ts, ts, new MediaTime(v.Duration, scale), v.Data.Length, m.Pos, m.Key, Convert.ToHexStringLower(digest)));
                i++;
            }
        }

        // Our audio packets only, so video is not counted as a difference.
        OurResult audioOnly = new() { Format = ours.Format, Tracks = ours.Tracks, Packets = [.. ours.Packets.Where(p => audio.Any(a => a.Index == p.Track))] };
        return Compare(file, theirs, audioOnly, exceptions);
    }

    /// <summary>Our single audio track from a Matroska file against OneAudio reading the Ogg file remuxed from it.</summary>
    public static FileReport CompareOgg(string matroskaFile, string oggFile, OurResult ours, Exceptions exceptions)
    {
        FileReport report = new() { File = matroskaFile, Format = ours.Format, Status = ours.Status, Message = ours.Message };
        if (ours.Status != "ok")
        {
            return report;
        }

        MediaTrack? audio = ours.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio);
        if (audio is null)
        {
            report.Status = "error";
            report.Message = "no audio track";
            return report;
        }

        List<PacketRecord> mine = [.. ours.Packets.Where(p => p.Track == audio.Index)];
        List<PacketRecord> theirs = [];
        using FileStream fs = File.OpenRead(oggFile);
        using OneAudio.Ogg.OggPacketSource source = OneAudio.Ogg.OggPacketSource.Open(fs, leaveOpen: true);
        int i = 0;
        byte[] digest = new byte[SHA256.HashSizeInBytes];
        while (source.TryReadPacket(out OneAudio.PacketView v))
        {
            if (v.IsHeader)
            {
                continue;
            }

            SHA256.HashData(v.Data, digest);
            PacketRecord m = i < mine.Count ? mine[i] : default;
            // Ogg packets carry no container timestamps here, so only payload size and bytes are compared.
            theirs.Add(new PacketRecord(audio.Index, m.Pts, m.Dts, m.Duration, v.Data.Length, m.Pos, m.Key, Convert.ToHexStringLower(digest)));
            i++;
        }

        OurResult audioOnly = new() { Format = ours.Format, Tracks = ours.Tracks, Packets = mine };
        return Compare(matroskaFile, theirs, audioOnly, exceptions);
    }

    private static FileReport Compare(string file, List<PacketRecord> theirs, OurResult ours, Exceptions exceptions) =>
        Comparer.Compare(file, [], theirs, ours, exceptions);

}

/// <summary>
/// Structure listings from MP4Box (<c>-diso</c>) and mkvinfo, reduced to (depth, type, total size) in file
/// order, for comparison with the trees our readers expose.
/// </summary>
internal static class StructureCompare
{
    /// <summary>Structure providers of our readers; add one line per reader as it lands.</summary>
    public static List<Func<IMediaSource, IStructureNode?>> Providers { get; } = [static source =>
    {
        // The tree reads its top-level boxes on first use; ask for them so the root has its children.
        RecursiveAI.OneMedia.IsoBmff.BoxTree tree = new(source);
        return new FileNode([.. tree.TopLevel]);
    }];

    internal readonly record struct Entry(int Depth, string Type, long Size);

    // The whole file as one node (type key 0) over the top-level boxes, as the comparison expects.
    private sealed class FileNode(IReadOnlyList<IStructureNode> children) : IStructureNode
    {
        public ulong TypeKey => 0;

        public long Offset => 0;

        public int HeaderSize => 0;

        public long PayloadSize => 0;

        public bool IsSizeInferred => false;

        public IReadOnlyList<IStructureNode> Children => children;
    }

    /// <summary>Box listing from MP4Box: every XML element carrying a <c>Type</c> attribute, in document order.</summary>
    public static List<Entry> Mp4BoxListing(string file)
    {
        List<Entry> list = [];
        using System.Diagnostics.Process p = Oracle.Start("MP4Box", ["-diso", "-std", file]);
        using XmlReader xr = XmlReader.Create(p.StandardOutput, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true });
        int depth = 0;
        Stack<bool> counted = new();
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element)
            {
                string? type = xr.GetAttribute("Type");
                string? size = xr.GetAttribute("Size");
                bool isBox = type is not null && size is not null;
                if (isBox)
                {
                    list.Add(new Entry(depth, type!, long.Parse(size!, CultureInfo.InvariantCulture)));
                }

                if (!xr.IsEmptyElement)
                {
                    counted.Push(isBox);
                    if (isBox)
                    {
                        depth++;
                    }
                }
            }
            else if (xr.NodeType == XmlNodeType.EndElement && counted.Count > 0 && counted.Pop())
            {
                depth--;
            }
        }

        p.WaitForExit();
        return list;
    }

    /// <summary>
    /// Element listing from <c>mkvinfo -v -z</c>: depth from the line prefix, total size from the trailing
    /// <c>size N data size M</c>. Elements mkvinfo marks as having skipped sub-entries are returned with the
    /// flag so callers can cut their own tree at the same place.
    /// </summary>
    public static List<(Entry Entry, bool SkipsChildren)> Mkvinfo(string file)
    {
        List<(Entry, bool)> list = [];
        string text = Oracle.Run("mkvinfo", "-v", "-z", file);
        Regex line = new(@"^(?<prefix>[| ]*)\+ (?<name>.*?)(?::.*?)? size (?<total>\d+) data size (?<data>\d+)$", RegexOptions.Compiled | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));
        foreach (string raw in text.Split('\n'))
        {
            Match m = line.Match(raw.TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }

            string name = m.Groups["name"].Value;
            list.Add((new Entry(m.Groups["prefix"].Length, name, long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)), raw.Contains("subentries will be skipped", StringComparison.Ordinal)));
        }

        return list;
    }

    /// <summary>Runs the comparison for one ISO file. Returns a message, or null when the trees agree.</summary>
    public static string? CompareIso(string file)
    {
        List<Entry> theirs;
        try
        {
            theirs = Mp4BoxListing(file);
        }
        catch (XmlException e)
        {
            return "MP4Box produced no box listing: " + e.Message;
        }

        // Sample entries (children of stsd) are read as format-model data here, not as a box tree: leave out what MP4Box lists inside them.
        int stsdDepth = -1;
        theirs = [.. theirs.Where(e =>
        {
            if (stsdDepth >= 0 && e.Depth <= stsdDepth)
            {
                stsdDepth = -1;
            }

            if (stsdDepth >= 0 && e.Depth > stsdDepth + 1)
            {
                return false;
            }

            if (e.Type == "stsd")
            {
                stsdDepth = e.Depth;
            }

            return true;
        })];

        if (Providers.Count == 0)
        {
            return $"no structure provider registered ({theirs.Count} boxes in MP4Box's listing)";
        }

        using FileMediaSource source = FileMediaSource.Open(file);
        foreach (Func<IMediaSource, IStructureNode?> provider in Providers)
        {
            IStructureNode? root = provider(source);
            if (root is null)
            {
                continue;
            }

            List<Entry> ours = [];
            foreach ((IStructureNode node, int depth) in root.Descendants())
            {
                if (node.TypeKey == 0)
                {
                    continue; // synthetic file node
                }

                ours.Add(new Entry(depth - 2, FourCc(node.TypeKey), node.HeaderSize + node.PayloadSize));
            }

            for (int i = 0; i < Math.Min(ours.Count, theirs.Count); i++)
            {
                if (ours[i] != theirs[i])
                {
                    return $"entry {i}: MP4Box {theirs[i]} vs ours {ours[i]}";
                }
            }

            return ours.Count == theirs.Count ? null : $"box count: MP4Box {theirs.Count} vs ours {ours.Count}";
        }

        return "no provider recognised the file";
    }

    /// <summary>
    /// Compares every element mkvinfo (<c>-a -P -z -v -v</c>) lists with a position and sizes against our element tree: same offset,
    /// same total size, same data size. Returns a message, or null when every listed element is found and agrees.
    /// </summary>
    public static string? CompareMatroska(string file)
    {
        string text = Oracle.Run("mkvinfo", "-a", "-P", "-z", "-v", "-v", file);
        using FileMediaSource source = FileMediaSource.Open(file);
        RecursiveAI.OneMedia.Matroska.EbmlDocument doc = RecursiveAI.OneMedia.Matroska.EbmlDocument.Open(source);
        Dictionary<long, IStructureNode> byOffset = [];
        foreach (RecursiveAI.OneMedia.Matroska.EbmlElement top in doc.Elements)
        {
            foreach ((IStructureNode node, int _) in ((IStructureNode)top).Descendants(new MediaLimits { MaxElementCount = 100_000_000 }))
            {
                byOffset[node.Offset] = node;
            }
        }

        Regex rx = new(@"at (\d+) size (\d+) data size (\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        int listed = 0, missing = 0, differing = 0;
        string? first = null;
        foreach (string line in text.Split('\n'))
        {
            Match m = rx.Match(line);
            if (!m.Success)
            {
                continue;
            }

            listed++;
            long at = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), size = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), data = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (!byOffset.TryGetValue(at, out IStructureNode? e))
            {
                missing++;
                first ??= "missing in tree: " + line.Trim();
            }
            else if (e.HeaderSize + e.PayloadSize != size || e.PayloadSize != data)
            {
                differing++;
                first ??= $"differs: {line.Trim()} | ours header={e.HeaderSize} payload={e.PayloadSize}";
            }
        }

        return missing + differing == 0 ? (listed == 0 ? "mkvinfo listed no elements" : null) : $"{listed} elements: missing {missing}, differing {differing}; first {first}";
    }

    private static string FourCc(ulong key)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            c[i] = (char)((key >> ((3 - i) * 8)) & 0xFF);
        }

        return new string(c);
    }
}
