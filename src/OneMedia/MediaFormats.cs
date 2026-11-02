using RecursiveAI.OneMedia.Asf;
using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Dv;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Sniffing;

namespace RecursiveAI.OneMedia;

/// <summary>
/// The formats the happy path (<see cref="Media"/>) opens. Formats are listed here by name, so a program that never calls the happy
/// path does not carry them. Programs that want a different set build their own <see cref="MediaFormatRegistry"/> and pass it to
/// <see cref="MediaFile.Open(IMediaSource, MediaFormatRegistry?, MediaOpenOptions?, CancellationToken)"/>.
/// </summary>
public static class MediaFormats
{
    /// <summary>
    /// The shared registry. It opens ISO BMFF (MP4, MOV, 3GP, fragmented MP4), Matroska/WebM, AVI, ASF, MPEG-TS and M2TS, MPEG program streams, FLV, Ogg, FLAC,
    /// WAVE (RIFF, RF64, Wave64), AIFF, CAF, DV, the elementary streams (MP3 with ID3, ADTS, AMR, AC-3/E-AC-3, IVF, Annex B H.264/HEVC/VVC, MPEG video, AV1 OBU)
    /// and HLS and DASH presentations. Data in none of these formats raises an <see cref="UnsupportedMediaException"/>.
    /// Do not add to it; use <see cref="Create"/> for that.
    /// </summary>
    public static MediaFormatRegistry Default { get; } = Create();

    /// <summary>Builds a new registry with the same contents as <see cref="Default"/>, for callers that want to add formats.</summary>
    /// <returns>A new registry.</returns>
    public static MediaFormatRegistry Create()
    {
        MediaFormatRegistry registry = CreateContainers();
        registry.Add(new Streaming.Sources.PresentationFormat());
        return registry;
    }

    /// <summary>The containers segments of a presentation are recognised as: everything in <see cref="Create"/> except playlists and manifests, which cannot nest.</summary>
    internal static MediaFormatRegistry SegmentFormats { get; } = CreateContainers();

    private static MediaFormatRegistry CreateContainers()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffFormat());
        registry.Add(new MatroskaFormat());
        registry.Add(new RecursiveAI.OneMedia.Riff.WavFileFormat());
        registry.Add(new AviFormat());
        registry.Add(new AsfFormat());
        registry.Add(new RecursiveAI.OneMedia.MpegTs.MpegTsFormat());
        registry.Add(new RecursiveAI.OneMedia.MpegPs.MpegPsFormat());
        registry.Add(new RecursiveAI.OneMedia.Flv.FlvFormat());
        registry.Add(new RecursiveAI.OneMedia.Ogg.OggFormat());
        registry.Add(new RecursiveAI.OneMedia.Flac.FlacFormat());
        registry.Add(new RecursiveAI.OneMedia.Aiff.AiffFileFormat());
        registry.Add(new RecursiveAI.OneMedia.Caf.CafFileFormat());
        registry.Add(new AudioStreams.Id3AudioFormat());
        registry.Add(new AudioStreams.MpegAudioFormat());
        registry.Add(new AudioStreams.AdtsFormat());
        registry.Add(new AudioStreams.AmrFormat());
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.IvfFormat());
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.H264));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.Hevc));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.Vvc));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.MpegVideo));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.Av1Obu));
        registry.Add(new AudioStreams.Ac3Format());
        registry.Add(new DvFormat());
        return registry;
    }
}
