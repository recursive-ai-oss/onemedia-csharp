using System.Linq;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Playback;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Judges hand-made file descriptions against every built-in browser profile, so the static profile tables and the checker run under
/// NativeAOT and in the browser WebAssembly runtime. Part of the smoke runs.
/// </summary>
internal static class PlaybackSmoke
{
    public static void Run(Action<string, bool> check)
    {
        PlaybackTrack video = new(0, TrackKind.Video, CodecId.H264, "avc1", "avc1.640028", 100, 40, 8, ChromaFormat.Yuv420, false);
        PlaybackTrack audio = new(1, TrackKind.Audio, CodecId.Aac, "mp4a", "mp4a.40.2", 2, -1, 0, ChromaFormat.Unknown, false);
        PlaybackDescription mp4 = new("mp4", "video/mp4", "video/mp4; codecs=\"avc1.640028,mp4a.40.2\"", false, true, [video, audio]);

        check("playback profiles listed", BrowserProfiles.All.Count == 8 && BrowserProfiles.Find("safari") is not null);
        check("playback chrome plays H.264 + AAC in MP4", Playability.Check(mp4, BrowserProfiles.Chrome).Progressive.Plays);
        PlaybackVerdict chromium = Playability.Check(mp4, BrowserProfiles.Chromium);
        check("playback chromium blocks H.264, remedy is a video blocker",
            !chromium.CanPlay && chromium.Blockers.Any(b => b.Code == "codec.unsupported" && b.Remedy == PlaybackRemedy.VideoBlocker));
        check("playback plain MP4 cannot go to MediaSource", Playability.Check(mp4, BrowserProfiles.GenericMp4).MediaSource.Blockers.Any(b => b.Code == "mediasource.not-segmented"));
    }
}
