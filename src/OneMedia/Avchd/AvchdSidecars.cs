using System.Globalization;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Avchd;

/// <summary>
/// Finds the clip information (<c>CLIPINF/NNNNN.CPI</c> or <c>.clpi</c>) and playlist (<c>PLAYLIST/*.MPL</c> or <c>.mpls</c>) files
/// that an AVCHD camera or a Blu-ray layout stores beside a transport stream under <c>BDMV/STREAM</c>. They are listed, never decoded:
/// no sample file or independent tool output was available to check a parser against, so each is exposed as a raw native entry whose key is
/// the file name and whose text is the path and size.
/// </summary>
internal static class AvchdSidecars
{
    private const int MaxFiles = 64;

    public static void AddTo(string? streamPath, List<ITagContainer> containers, TagIo io)
    {
        if (string.IsNullOrEmpty(streamPath))
        {
            return;
        }

        List<NativeTag> tags = [];
        try
        {
            string? streamDir = Path.GetDirectoryName(Path.GetFullPath(streamPath));
            if (streamDir is null || !string.Equals(Path.GetFileName(streamDir), "STREAM", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? root = Path.GetDirectoryName(streamDir);
            if (root is null)
            {
                return;
            }

            string stem = Path.GetFileNameWithoutExtension(streamPath);
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(dir);
                bool clips = string.Equals(name, "CLIPINF", StringComparison.OrdinalIgnoreCase);
                bool lists = string.Equals(name, "PLAYLIST", StringComparison.OrdinalIgnoreCase);
                if (!clips && !lists)
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(file);
                    bool isClip = clips && (ext.Equals(".cpi", StringComparison.OrdinalIgnoreCase) || ext.Equals(".clpi", StringComparison.OrdinalIgnoreCase))
                        && Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase);
                    bool isList = lists && (ext.Equals(".mpl", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mpls", StringComparison.OrdinalIgnoreCase));
                    if ((isClip || isList) && tags.Count < MaxFiles)
                    {
                        long size = new FileInfo(file).Length;
                        NativeTag t = new(io, MpegTs.MpegTsTagSystems.Sidecars, Path.GetFileName(file), 0, TagValueKind.Text, 0, 0, null, ByteRange.None, ByteRange.None);
                        t.SetText(file + " (" + size.ToString(CultureInfo.InvariantCulture) + " bytes)");
                        tags.Add(t);
                    }
                }
            }
        }
        catch (IOException)
        {
            // A directory that cannot be listed simply has no sidecars to report.
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (tags.Count > 0)
        {
            containers.Add(new MpegTs.TsTagContainer(MpegTs.MpegTsTagSystems.Sidecars, 0, ByteRange.None, tags));
        }
    }
}
