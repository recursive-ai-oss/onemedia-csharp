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
                return new RecursiveAI.OneMedia.Matroska.MatroskaMuxer(
                    output,
                    new RecursiveAI.OneMedia.Matroska.MatroskaMuxOptions
                    {
                        Profile = format == RemuxFormat.WebM ? RecursiveAI.OneMedia.Matroska.MatroskaProfile.WebM : RecursiveAI.OneMedia.Matroska.MatroskaProfile.Matroska,
                        // Matroska counts nanoseconds from 2001-01-01 UTC (RFC 9559 DateUTC); a tick is 100 ns.
                        DateUtcNanoseconds = options.CreationTime is { } t ? (t.UtcTicks - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) * 100 : null,
                    },
                    options.Limits);
            case RemuxFormat.MpegTs or RemuxFormat.M2ts:
                return new RecursiveAI.OneMedia.MpegTs.MpegTsMuxer(
                    output,
                    new RecursiveAI.OneMedia.MpegTs.MpegTsMuxOptions
                    {
                        Layout = format == RemuxFormat.M2ts ? RecursiveAI.OneMedia.MpegTs.TsPacketLayout.M2ts192 : RecursiveAI.OneMedia.MpegTs.TsPacketLayout.Ts188,
                        Limits = options.Limits,
                    });
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
                        MovieTimescale = options.MovieTimescale,
                        Layout = layout,
                        Cmaf = format == RemuxFormat.Cmaf,
                        Limits = options.Limits,
                        CreationTime = options.CreationTime,
                    });
                }
        }
    }
}
