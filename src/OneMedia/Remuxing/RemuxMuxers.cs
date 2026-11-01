using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>Creates the muxer for a <see cref="RemuxFormat"/>. The one place that knows which containers can be written.</summary>
internal static class RemuxMuxers
{
    public static IMuxer Create(RemuxFormat format, Stream output, RemuxOptions options)
    {
        switch (format)
        {
            case RemuxFormat.Matroska or RemuxFormat.WebM:
                throw new UnsupportedMediaException($"Writing {format} is not available yet.");
            default:
                {
                    IsoFileKind kind = format switch
                    {
                        RemuxFormat.M4a => IsoFileKind.M4a,
                        RemuxFormat.M4v => IsoFileKind.M4v,
                        RemuxFormat.Mov => IsoFileKind.Mov,
                        RemuxFormat.ThreeGp => IsoFileKind.ThreeGp,
                        RemuxFormat.ThreeG2 => IsoFileKind.ThreeG2,
                        _ => IsoFileKind.Mp4,
                    };
                    IsoLayout layout = format is RemuxFormat.FragmentedMp4 or RemuxFormat.Cmaf
                        ? IsoLayout.Fragmented
                        : options.Faststart ? IsoLayout.MoovFirst : IsoLayout.MoovLast;
                    return new IsoMuxer(output, new IsoMuxOptions
                    {
                        Kind = kind,
                        Layout = layout,
                        Cmaf = format == RemuxFormat.Cmaf,
                        Limits = options.Limits,
                        CreationTime = options.CreationTime,
                    });
                }
        }
    }
}
