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
    /// The shared registry: ISO BMFF and Matroska/WebM can be opened; every other format OneMedia knows is recognised by content, so
    /// opening one reports the format by name in an <see cref="UnsupportedMediaException"/>. Do not add to it; use <see cref="Create"/> for that.
    /// </summary>
    public static MediaFormatRegistry Default { get; } = Create();

    /// <summary>Builds a new registry with the same contents as <see cref="Default"/>, for callers that want to add formats.</summary>
    /// <returns>A new registry.</returns>
    public static MediaFormatRegistry Create()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffFormat());
        registry.Add(new MatroskaFormat());
        registry.Add(new WavSniffer());
        registry.Add(new AviFormat());
        registry.Add(new AsfSniffer());
        registry.Add(new MpegTsSniffer());
        registry.Add(new MpegPsSniffer());
        registry.Add(new FlvSniffer());
        registry.Add(new OggSniffer());
        registry.Add(new FlacSniffer());
        registry.Add(new AiffSniffer());
        registry.Add(new CafSniffer());
        registry.Add(new Id3Sniffer());
        registry.Add(new MpegAudioSniffer());
        registry.Add(new AdtsSniffer());
        registry.Add(new AmrSniffer());
        registry.Add(new IvfSniffer());
        registry.Add(new DvFormat());
        return registry;
    }
}
