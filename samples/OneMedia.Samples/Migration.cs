using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Samples;

/// <summary>
/// The snippets shown on the migration pages (docs/migration), compiled and run here so they cannot drift from the API.
/// Each entry is named after the page it belongs to; the page repeats the lines between the "page:" comments.
/// </summary>
internal static class Migration
{
    public static readonly (string Name, Func<string, string> Run)[] All =
    [
        ("migration/ffprobe: show_format, show_streams", dir =>
        {
            // page: ffmpeg-ffprobe
            using var media = Media.Open("in.mp4".Under(dir));
            string line = $"{media.Format}, {media.ExactDuration}, {media.Tracks.Count} tracks";
            foreach (MediaTrack track in media.Tracks)
                line += $"; #{track.Index} {track.Kind} {track.Codec} {track.Rfc6381Codec()} {track.Language}";
            return line;
        }),
        ("migration/ffprobe: show_packets", dir =>
        {
            // page: ffmpeg-ffprobe
            using var media = Media.Open("in.mp4".Under(dir));
            int packets = 0, keyframes = 0;
            foreach (OwnedPacket packet in media.Packets(media.Video!))
            {
                using (packet) { packets++; if (packet.Info.IsKeyframe) keyframes++; }
                // packet.Info.Pts / Dts / Duration are MediaTime (ticks + timescale), packet.Data the payload bytes.
            }
            return $"{packets} packets, {keyframes} keyframes";
        }),
        ("migration/ffmpeg: -c copy, -ss -to, +faststart, -metadata", dir =>
        {
            // page: ffmpeg-ffprobe
            RecursiveAI.OneMedia.Remuxing.RemuxReport remux = Media.Remux("in.mp4".Under(dir), "out.mkv".Under(dir));
            var clip = Media.Trim("in.mp4".Under(dir), "clip.mp4".Under(dir), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
            Media.Faststart("in.mp4".Under(dir), "fast.mp4".Under(dir));
            using (MediaFile media = Media.Open("in.mp4".Under(dir)))
            {
                media.Tags.Title = "Hello";
                media.SaveAs("titled.mp4".Under(dir));
            }
            return $"remux losses: {remux.HasLosses}; clip {clip.Duration.ToTimeSpan()}";
        }),
        ("migration/ffmpeg: hls, subtitles", dir =>
        {
            // page: ffmpeg-ffprobe
            Media.PackageHls("in.mp4".Under(dir), "hls2".Under(dir));
            return Subtitles.Convert("sample.srt".Under(dir), "migrated.vtt".Under(dir)).IsLossless.ToString();
        }),
        ("migration/taglib: read and write", dir =>
        {
            // page: taglib-sharp
            using MediaFile media = Media.Open("in.mp4".Under(dir));
            string? title = media.Tags.Title;
            media.Tags.Title = "New title";
            media.Tags.Artist = "New artist";
            media.Tags.TrackNumber = 3;
            TagWriteReport report = media.SaveAs("tagged.mp4".Under(dir));
            return $"was '{title}', wrote {report.Systems.Count} system(s), {report.Warnings.Count} warning(s)";
        }),
        ("migration/taglib: pictures", dir =>
        {
            // page: taglib-sharp
            using MediaFile media = Media.Open("in.mp4".Under(dir));
            media.Tags.SetPicture(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "image/jpeg", PictureType.FrontCover);
            media.SaveAs("cover.mp4".Under(dir));
            using MediaFile again = Media.Open("cover.mp4".Under(dir));
            return again.Tags.Pictures.Count + " picture(s)";
        }),
        ("migration/atl: read tags and properties", dir =>
        {
            // page: atl-net
            using MediaFile media = Media.Open("aac.m4a".Under(dir));
            MediaTrack audio = media.Audio!;
            return $"{media.Tags.Title} / {media.Tags.Album} / {audio.Audio!.Value.SampleRate} Hz / {audio.BitRate} bit/s / {media.Duration}";
        }),
        ("migration/atl: write in place", dir =>
        {
            // page: atl-net
            File.Copy("aac.m4a".Under(dir), "atl.m4a".Under(dir), overwrite: true);
            using MediaFile media = Media.Open("atl.m4a".Under(dir));
            media.Tags.Title = "Edited";
            try { media.SaveInPlace(); return "in place"; }
            catch (PaddingRequiredException) { media.Save(); return "no padding: atomic replace instead"; }
        }),
        ("migration/metadataextractor: facts and native tags", dir =>
        {
            // page: metadataextractor
            using MediaFile media = Media.Open("located.mp4".Under(dir));
            Fact<MediaDateTime> captured = media.CaptureTime;
            foreach (FactCandidate<MediaDateTime> candidate in captured.Candidates)
                _ = $"{candidate.Source} {candidate.RawValue} {candidate.Interpretation} {candidate.Certainty}";
            NativeTag? tag = media.Native["QuickTime", "©xyz"];
            return $"{captured.Candidates.Count} candidates; location {media.Location}; ©xyz {tag?.ToString() ?? "absent"}; defects {media.Defects.Count}";
        }),
        ("migration/mediainfo: general and tracks", dir =>
        {
            // page: mediainfo
            using MediaFile media = Media.Open("in.mp4".Under(dir));
            MediaTrack? video = media.Video;
            return $"{media.Format} {media.Duration} {video?.Codec} {video?.Video?.Width}x{video?.Video?.Height} {video?.Video?.FrameRateNumerator}/{video?.Video?.FrameRateDenominator}";
        }),
        ("migration/exiftool: read, set date, strip", dir =>
        {
            // page: exiftool
            using (MediaFile media = Media.Open("located.mp4".Under(dir)))
                _ = media.Native["QuickTime", "©xyz"];
            Media.SetCaptureTime("in.mp4".Under(dir), "dated.mp4".Under(dir), new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.FromHours(2)));
            Media.StripLocation("located.mp4".Under(dir), "nolocation.mp4".Under(dir));
            Media.StripDeviceIds("located.mp4".Under(dir), "nodevice.mp4".Under(dir));
            using MediaFile dated = Media.Open("dated.mp4".Under(dir));
            return dated.CaptureTime.ToString();
        }),
    ];

    // The pages use short names; the runner keeps them in the scratch directory under the fixtures' names.
    private static string Under(this string name, string dir) => Path.Combine(dir, name == "in.mp4" ? "tiny.mp4" : name);
}
