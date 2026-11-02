using System.Security.Cryptography;
using OneAudio;
using RecursiveAI.OneMedia.Audio;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// What a phone app does with the library, on top of the shared checks: reads a file from app storage, edits its title with SaveAs and proves
/// the media payload is untouched, remuxes to Matroska, and decodes audio to PCM. Files live in the app's own writable directory.
/// </summary>
internal static class MobileChecks
{
    public static void Run(Action<string, bool> check, string storage)
    {
        string dir = Path.Combine(storage, "onemedia-mobile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "in.m4a");
            File.WriteAllBytes(input, Resource("aac.m4a"));

            // Tag edit to a new file: the title changes, every track's packets keep the same bytes.
            string titled = Path.Combine(dir, "titled.m4a");
            using (MediaFile media = Media.Open(input))
            {
                media.Tags.Title = "Mobile smoke";
                media.SaveAs(titled);
            }

            using (MediaFile before = Media.Open(input))
            using (MediaFile after = Media.Open(titled))
            {
                check("mobile: SaveAs title", after.Tags.Title == "Mobile smoke" && File.GetLastWriteTimeUtc(titled) > DateTime.MinValue);
                check("mobile: SaveAs payload hash unchanged", before.Audio is not null && after.Audio is not null && PayloadHash(before, before.Audio) == PayloadHash(after, after.Audio));
            }

            // Remux to Matroska; the packets come out byte-equal.
            string mka = Path.Combine(dir, "out.mka");
            Remuxing.RemuxReport report = Media.Remux(input, mka);
            using (MediaFile before = Media.Open(input))
            using (MediaFile after = Media.Open(mka))
            {
                check("mobile: remux m4a to mka", after.Format == MediaFormatId.Matroska && report is not null && after.Audio is not null && PayloadHash(before, before.Audio!) == PayloadHash(after, after.Audio));
            }

            // Audio to PCM through the OneAudio adapter, from a file in app storage.
            using (FileStream stream = File.OpenRead(titled))
            using (IAudioDecoder decoder = AudioDecoder.Open(stream, MediaAudioExtensions.CreateRegistry(), leaveOpen: true))
            {
                float[] buf = new float[4096 * decoder.Format.Channels];
                long frames = 0;
                bool finite = true;
                int got;
                while ((got = decoder.Read(buf)) > 0)
                {
                    frames += got;
                    for (int i = 0; i < got * decoder.Format.Channels; i++)
                    {
                        finite &= float.IsFinite(buf[i]);
                    }
                }

                check("mobile: decode aac to pcm", frames == 6400 && finite);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // SHA-256 over the packet payloads of one track, in order.
    private static string PayloadHash(MediaFile file, MediaTrack track)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (OwnedPacket p in file.Packets(track))
        {
            using (p)
            {
                hash.AppendData(p.Data.Span);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(MobileChecks).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream m = new();
        s.CopyTo(m);
        return m.ToArray();
    }
}
