using System.Security.Cryptography;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Compares the audio packets of single-stream Ogg files (Vorbis, Opus, FLAC) with OneAudio's <c>OggPacketSource</c>: payload size and SHA-256 of every
/// packet, and, where OneAudio states a granule position for a packet (the last one that ends on a page), our end time against it.
/// </summary>
internal static class OggCompare
{
    public static int Run(IReadOnlyList<string> files)
    {
        long packets = 0, sizeDiff = 0, hashDiff = 0, granules = 0, granuleDiff = 0;
        int bad = 0;
        foreach (string file in files)
        {
            using IDemuxer d = Readers.Create().Open(FileMediaSource.Open(file));
            MediaTrack? audio = d.Tracks.Count == 1 && d.Tracks[0].Kind == TrackKind.Audio ? d.Tracks[0] : null;
            if (audio is null)
            {
                Console.WriteLine($"skip {Path.GetFileName(file)}: not a single audio stream");
                continue;
            }

            // The granule of an Opus stream counts the pre-skip (RFC 7845 §4); ours starts after it.
            long origin = audio.Codec.Name == "opus" && audio.Edit.Priming.IsKnown ? audio.Edit.Priming.Rescale(audio.Timescale, MediaRounding.NearestAwayFromZero).Ticks : 0;
            List<(int Size, string Hash, long End)> mine = [];
            while (d.TryReadPacket(out PacketView p))
            {
                MediaTime end = p.Info.Pts + p.Info.Duration;
                mine.Add((p.Data.Length, Convert.ToHexString(SHA256.HashData(p.Data)), end.IsKnown ? end.Rescale(audio.Timescale, MediaRounding.NearestAwayFromZero).Ticks + origin : long.MinValue));
            }

            using FileStream fs = File.OpenRead(file);
            OneAudio.Ogg.OggPacketSource source;
            try
            {
                source = OneAudio.Ogg.OggPacketSource.Open(fs, leaveOpen: true);
            }
            catch (OneAudio.AudioFormatException e)
            {
                // OneAudio's reader wants an Ogg page at offset 0; a file with a leading ID3v2 block is read by ours only.
                Console.WriteLine($"skip {Path.GetFileName(file)}: OneAudio cannot open it ({e.Message})");
                continue;
            }

            using OneAudio.Ogg.OggPacketSource owned = source;
            int i = 0;
            long fSize = 0, fHash = 0, fGranule = 0, fileGranules = 0;
            while (owned.TryReadPacket(out OneAudio.PacketView v))
            {
                if (v.IsHeader)
                {
                    continue;
                }

                if (i >= mine.Count)
                {
                    fSize++;
                    i++;
                    continue;
                }

                (int size, string hash, long end) = mine[i++];
                fSize += size == v.Data.Length ? 0 : 1;
                fHash += hash == Convert.ToHexString(SHA256.HashData(v.Data)) ? 0 : 1;
                if (v.EndFrame >= 0)
                {
                    fileGranules++;
                    fGranule += end == v.EndFrame ? 0 : 1;
                }
            }

            fSize += i == mine.Count ? 0 : Math.Abs(mine.Count - i);
            packets += mine.Count;
            sizeDiff += fSize;
            hashDiff += fHash;
            granules += fileGranules;
            granuleDiff += fGranule;
            bool ok = fSize == 0 && fHash == 0 && fGranule == 0;
            bad += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "ok  " : "DIFF")} {Path.GetFileName(file)}: {audio.Codec.Name}, {mine.Count} packets (ours) / {i} (OneAudio); size differences {fSize}, hash differences {fHash}; granules {fileGranules}, differing {fGranule}");
        }

        Console.WriteLine($"oneaudio-ogg: {files.Count} files, {packets} packets, size differences {sizeDiff}, hash differences {hashDiff}, granules compared {granules}, differing {granuleDiff}");
        return bad == 0 ? 0 : 1;
    }
}
