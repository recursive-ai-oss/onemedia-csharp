using System;
using System.Collections.Generic;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the bitmap subtitle decoders. <c>bitmap-pgs</c> takes a mode byte (bit 0: a whole .sup file, else container packets) and the
/// data; <c>bitmap-spu</c> takes one DVD sub-picture unit; <c>bitmap-dvb</c> takes PES payloads; <c>bitmap-idx</c> takes a VobSub index
/// followed after a NUL byte by a program stream. None may throw anything but <see cref="MediaLimitExceededException"/>, and every picture
/// must have the size its pixel data says. Limits are small so that a hostile size fails fast.
/// </summary>
internal static class BitmapSubtitleTargets
{
    private static readonly BitmapSubtitleOptions Options = new() { Limits = new MediaLimits { MaxAllocationBytes = 4L << 20 } };

    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["bitmap-pgs"] = Pgs;
        targets["bitmap-spu"] = Spu;
        targets["bitmap-dvb"] = Dvb;
        targets["bitmap-idx"] = Idx;
    }

    private static void Check(IEnumerable<SubtitleCue> cues)
    {
        foreach (SubtitleCue c in cues)
        {
            foreach (SubtitleImage i in c.Images)
            {
                if (i.Width < 1 || i.Height < 1 || i.Stride != i.Width * 4 || i.Pixels.Length != i.Stride * i.Height)
                {
                    throw new InvalidOperationException("image size disagrees with its pixels");
                }
            }
        }
    }

    private static void Pgs(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        try
        {
            if ((data[0] & 1) != 0)
            {
                Check(new PgsDecoder(Options).FeedSup(data[1..]));
                return;
            }

            var d = new PgsDecoder(Options);
            // Split the rest into packets at 0x80 end segments' worth of bytes to exercise state carried between calls.
            ReadOnlySpan<byte> rest = data[1..];
            int step = Math.Max(16, rest.Length / 4);
            for (int p = 0; p < rest.Length; p += step)
            {
                Check(d.Feed(rest.Slice(p, Math.Min(step, rest.Length - p)), new MediaTime(p, 1000), new MediaTime(500, 1000)));
            }

            Check(d.Flush());
        }
        catch (MediaLimitExceededException)
        {
        }
    }

    private static void Spu(ReadOnlySpan<byte> data)
    {
        try
        {
            Check(new VobSubDecoder(Options).Decode(data, new MediaTime(1, 1000)));
        }
        catch (MediaLimitExceededException)
        {
        }
    }

    private static void Dvb(ReadOnlySpan<byte> data)
    {
        try
        {
            var d = new DvbSubtitleDecoder(Options);
            int step = Math.Max(16, data.Length / 3);
            for (int p = 0; p < data.Length; p += step)
            {
                Check(d.Feed(data.Slice(p, Math.Min(step, data.Length - p)), new MediaTime(p, 1000)));
            }

            Check(d.Flush());
        }
        catch (MediaLimitExceededException)
        {
        }
    }

    private static void Idx(ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        ReadOnlySpan<byte> idx = nul < 0 ? data : data[..nul];
        ReadOnlySpan<byte> sub = nul < 0 ? default : data[(nul + 1)..];
        try
        {
            Check(VobSubReader.Read(idx, sub, Options));
            Check(VobSubReader.ReadProgramStream(sub, Options));
        }
        catch (MediaLimitExceededException)
        {
        }
    }
}
