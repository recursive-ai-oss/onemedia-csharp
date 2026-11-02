using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Playback;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Samples;

/// <summary>The happy-path tasks, each one or two lines inside its method. The runner prints what each returns.</summary>
internal static class Tasks
{
    public static readonly (string Name, Func<string, string> Run)[] All =
    [
        ("open and facts", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "tiny.mp4"));
            return $"{media.Format} {media.Duration} capture={media.CaptureTime} candidates={media.CaptureTime.Candidates.Count} device={media.Device.Make}";
        }),
        ("location", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "located.mp4"));
            return media.Location.HasValue ? media.Location.Value.ToString()! : "none";
        }),
        ("telemetry", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "gpmf.mp4"));
            return $"{media.Telemetry.Count} channel(s), first {(media.Telemetry.Count > 0 ? media.Telemetry[0].Name : "")}";
        }),
        ("video and audio facts", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "tiny.mp4"));
            return $"{media.Video?.Width}x{media.Video?.Height} {media.Video?.Codec} / {media.Audio?.Codec} {media.Audio?.SampleRate} Hz; {media.Tracks.Count} tracks";
        }),
        ("subtitle tracks", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "captioned.mkv"));
            return $"{media.Subtitles.Count} track(s), {media.Subtitles[0].Document.Cues.Count} cue(s)";
        }),
        ("native escape hatch", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "tiny.mp4"));
            return media.Native["QuickTime", "©day"]?.ToString() ?? "no ©day";
        }),
        ("edit tags: Save", dir =>
        {
            string path = Path.Combine(dir, "save.mp4");
            File.Copy(Path.Combine(dir, "tiny.mp4"), path);
            using (var media = Media.Open(path)) { media.Tags.Title = "Deposition, day 2"; media.Save(); }
            using var again = Media.Open(path);
            return again.Tags.Title ?? "lost";
        }),
        ("edit tags: SaveAs", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "tiny.mp4"));
            media.Tags.Title = "Copy"; media.Tags.Artist = "Someone";
            return $"{media.SaveAs(Path.Combine(dir, "saveas.mp4")).Systems.Count} system(s) written";
        }),
        ("edit tags: SaveInPlace", dir =>
        {
            string path = Path.Combine(dir, "inplace.mp4");
            File.Copy(Path.Combine(dir, "tiny.mp4"), path);
            using var media = Media.Open(path);
            media.Tags.Title = "x";
            // Only within existing padding: a file without room refuses and names how much it needs.
            try { media.SaveInPlace(); return "written in place"; }
            catch (PaddingRequiredException e) { return "padding required: " + e.Message; }
        }),
        ("packet loop", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "tiny.mp4"));
            long bytes = 0;
            foreach (var packet in media.Packets(media.Video!)) { bytes += packet.Data.Length; packet.Dispose(); }
            return bytes + " video bytes";
        }),
        ("remux", dir => $"losses: {Media.Remux(Path.Combine(dir, "tiny.mp4"), Path.Combine(dir, "remux.mkv")).HasLosses}"),
        ("trim", dir => Media.Trim(Path.Combine(dir, "tiny.mp4"), Path.Combine(dir, "clip.mp4"), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5)).Duration.ToTimeSpan().ToString()),
        ("make playable", dir => $"after: {Media.MakePlayable(Path.Combine(dir, "tiny.mp4"), Path.Combine(dir, "playable.mp4"), BrowserProfiles.Chrome).After?.CanPlay}"),
        ("repair", dir =>
        {
            byte[] whole = File.ReadAllBytes(Path.Combine(dir, "tiny.mp4"));
            File.WriteAllBytes(Path.Combine(dir, "cutoff.mp4"), whole.AsSpan(0, whole.Length * 3 / 4).ToArray());
            return Media.Repair(Path.Combine(dir, "cutoff.mp4"), Path.Combine(dir, "fixed.mp4")).Status.ToString();
        }),
        ("strip location", dir =>
        {
            Media.StripLocation(Path.Combine(dir, "located.mp4"), Path.Combine(dir, "stripped.mp4"));
            using var media = Media.Open(Path.Combine(dir, "stripped.mp4"));
            return media.Location.HasValue ? "still located" : "no location";
        }),
        ("package HLS", dir =>
        {
            var report = Media.PackageHls(Path.Combine(dir, "tiny.mp4"), Path.Combine(dir, "hls"));
            return string.Join(",", report.Manifests);
        }),
        ("subtitles convert", dir => Subtitles.Convert(Path.Combine(dir, "sample.srt"), Path.Combine(dir, "sample.vtt")).Losses.Count + " loss(es)"),
        ("audio decode", dir =>
        {
            using var media = Media.Open(Path.Combine(dir, "aac.m4a"));
            using var pcm = media.Audio!.Decode();
            Span<float> buffer = new float[4096];
            return $"{pcm.Format.SampleRate} Hz, first read {pcm.Read(buffer)} samples";
        }),
    ];
}
