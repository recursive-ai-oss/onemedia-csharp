using System.Numerics;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>Turns the header models of one stream into the neutral <see cref="MediaTrack"/>.</summary>
internal static class AviTracks
{
    /// <summary>
    /// The frame rate of a DV type 1 stream is the DV system's (30000/1001 or 25), whatever rounding the writer put in the stream header:
    /// a header of 333666/10000000 is the NTSC rate stated to seven digits. A rate within a tenth of a percent of one of the two is replaced.
    /// </summary>
    internal static (long Scale, long Rate) DvFrameRate(AviStreamHeader h, long scale, long rate)
    {
        if (h.Type != AviStreamHeader.Iavs || scale <= 0 || rate <= 0)
        {
            return (scale, rate);
        }

        // |rate/scale - n/d| <= n/d/1000  <=>  |rate*d - n*scale| * 1000 <= n*scale
        foreach ((long n, long d) in new[] { (30000L, 1001L), (25L, 1L) })
        {
            if (Math.Abs((rate * d) - (n * scale)) * 1000 <= n * scale)
            {
                return (d, n);
            }
        }

        return (scale, rate);
    }

    public static MediaTrack Build(AviStreamInfo s, AviHeaderList headers)
    {
        AviStreamHeader h = s.Header;
        long scale = h.Scale;
        long rate = h.Rate;
        if (scale == 0 || rate == 0)
        {
            scale = 1;
            rate = 1000;
        }

        (scale, rate) = DvFrameRate(h, scale, rate);
        long g = (long)BigInteger.GreatestCommonDivisor(scale, rate);
        scale /= g;
        rate /= g;

        // The track clock: a tick lasts scale/rate seconds, so times are expressed in a timescale of rate.
        MediaTime duration = h.Length > 0 ? new MediaTime(h.Length * scale, rate) : MediaTime.Unknown;
        TrackKind kind = h.Type == AviStreamHeader.Vids || h.Type == AviStreamHeader.Iavs ? TrackKind.Video
            : h.Type == AviStreamHeader.Auds ? TrackKind.Audio
            : h.Type == AviStreamHeader.Txts ? TrackKind.Subtitle
            : TrackKind.Data;
        TrackDisposition disposition = (h.Flags & AviStreamHeader.Disabled) != 0 ? TrackDisposition.Disabled : TrackDisposition.None;
        TrackEditInfo edit = h.Start != 0 ? new TrackEditInfo(StartTime: new MediaTime(h.Start * scale, rate)) : default;

        CodecId codec = CodecId.Unknown;
        string? tag = null;
        CodecConfiguration config = default;
        AudioTrackInfo? audio = null;
        VideoTrackInfo? video = null;
        long bitRate = 0;
        if (h.Type == AviStreamHeader.Vids)
        {
            video = VideoInfo(s, headers, scale, rate, out codec, out tag);
            config = new CodecConfiguration(s.Format, "bitmapinfoheader");
        }
        else if (h.Type == AviStreamHeader.Iavs)
        {
            // DV type 1: the stream's chunks are whole DV frames, picture and sound together (docs/avi.md).
            codec = new CodecId("dvvideo");
            tag = h.HandlerText;
            bool pal = rate == 25 * scale;
            video = new VideoTrackInfo(720, pal ? 576 : 480, FrameRateNumerator: (int)Math.Min(rate, int.MaxValue), FrameRateDenominator: (int)Math.Min(scale, int.MaxValue));
            config = new CodecConfiguration(s.Format, "dvinfo");
        }
        else if (h.Type == AviStreamHeader.Auds)
        {
            audio = AudioInfo(s, out codec, out tag, out config, out bitRate);
        }

        return new MediaTrack
        {
            Index = s.Index,
            Id = s.Index,
            Kind = kind,
            Codec = codec,
            CodecTag = tag,
            Configuration = config,
            Timescale = rate,
            Duration = duration,
            Edit = edit,
            Name = string.IsNullOrEmpty(s.Name) ? null : s.Name,
            Disposition = disposition,
            Audio = audio,
            Video = video,
            BitRate = bitRate,
        };
    }

    private static VideoTrackInfo VideoInfo(AviStreamInfo s, AviHeaderList headers, long scale, long rate, out CodecId codec, out string? tag)
    {
        codec = CodecId.Unknown;
        tag = null;
        int width = (int)Math.Min(headers.Main.Width, int.MaxValue);
        int height = (int)Math.Min(headers.Main.Height, int.MaxValue);
        if (BitmapInfoHeader.TryParse(s.Format, out BitmapInfoHeader bmp))
        {
            // A negative biHeight means top-down rows; the magnitude of int.MinValue does not fit, so it is clamped.
            width = bmp.Width == int.MinValue ? int.MaxValue : Math.Abs(bmp.Width);
            height = bmp.Height == int.MinValue ? int.MaxValue : Math.Abs(bmp.Height);
            uint comp = bmp.Compression;
            string text = bmp.CompressionText;
            if (comp == 0 || comp == 3)
            {
                codec = AviCodecMap.RawBitmap();
                tag = comp == 0 ? "RGB" : "BITFIELDS";
            }
            else if (comp is 1 or 2)
            {
                codec = new CodecId("msrle");
                tag = comp == 1 ? "RLE8" : "RLE4";
            }
            else
            {
                codec = AviCodecMap.Video(text);
                tag = text;
            }
        }

        if (codec.IsUnknown)
        {
            // Some files leave the bitmap's compression field empty or unknown and name the codec only in the stream header.
            CodecId viaHandler = AviCodecMap.Video(s.Header.HandlerText);
            if (!viaHandler.IsUnknown)
            {
                codec = viaHandler;
                tag ??= s.Header.HandlerText;
            }

            tag ??= s.Header.Handler == 0 ? null : s.Header.HandlerText;
        }

        int sarN = 1;
        int sarD = 1;
        if (s.Properties is { AspectX: > 0, AspectY: > 0 } p && width > 0 && height > 0)
        {
            // dwFrameAspectRatio is the display aspect; the pixel aspect is that ratio divided by the frame's own width:height.
            long n = (long)p.AspectX * height;
            long d = (long)p.AspectY * width;
            long g = (long)BigInteger.GreatestCommonDivisor(n, d);
            if (g > 0 && n / g <= int.MaxValue && d / g <= int.MaxValue)
            {
                sarN = (int)(n / g);
                sarD = (int)(d / g);
            }
        }

        return new VideoTrackInfo(width, height, 0, sarN, sarD, (int)Math.Min(rate, int.MaxValue), (int)Math.Min(scale, int.MaxValue));
    }

    private static AudioTrackInfo? AudioInfo(AviStreamInfo s, out CodecId codec, out string? tag, out CodecConfiguration config, out long bitRate)
    {
        codec = CodecId.Unknown;
        tag = null;
        config = new CodecConfiguration(s.Format, "waveformatex");
        bitRate = 0;
        if (!WaveFormat.TryParse(s.Format, out WaveFormat wf))
        {
            return null;
        }

        codec = AviCodecMap.Audio(wf.FormatTag);
        tag = "0x" + wf.FormatTag.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        bitRate = wf.AverageBytesPerSecond * 8L;
        if (codec == CodecId.Aac && wf.FormatTag == 0x00FF && s.Format.Length > WaveFormat.Size && wf.ExtraSize > 0)
        {
            // Raw AAC in AVI carries the AudioSpecificConfig as the format's extra bytes.
            int extra = Math.Min(wf.ExtraSize, s.Format.Length - WaveFormat.Size);
            config = new CodecConfiguration(s.Format.AsMemory(WaveFormat.Size, extra), "asc");
        }

        return new AudioTrackInfo((int)Math.Min(wf.SamplesPerSecond, int.MaxValue), wf.Channels, wf.BitsPerSample);
    }
}
