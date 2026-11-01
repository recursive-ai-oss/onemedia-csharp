using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.MpegAudio;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>Frame header readers for audio streams that a container stores as one run of bytes.</summary>
internal static class FrameParsers
{
    // ISO/IEC 11172-3 §2.4.1: a frame header gives the frame length; free-format frames (bit rate 0) have no length to read.
    public static readonly FrameParser MpegAudio = static (ReadOnlySpan<byte> head, out int length, out int frames) =>
    {
        length = 0;
        frames = 0;
        if (!MpegAudioFrameHeader.TryParse(head, out MpegAudioFrameHeader h) || h.FrameLength <= 4)
        {
            return false;
        }

        length = h.FrameLength;
        frames = h.SamplesPerFrame;
        return true;
    };

    // ETSI TS 102 366 §4.4: the syncframe header gives the frame size.
    public static readonly FrameParser Ac3 = static (ReadOnlySpan<byte> head, out int length, out int frames) =>
    {
        length = 0;
        frames = 0;
        if (!Ac3SyncFrame.TryParse(head, out Ac3SyncFrame f) || f.FrameSize <= 0)
        {
            return false;
        }

        length = f.FrameSize;
        frames = f.SamplesPerFrame;
        return true;
    };

    // ISO/IEC 13818-7 §6.2: the ADTS header gives the frame length; each raw data block is 1024 samples.
    public static readonly FrameParser Adts = static (ReadOnlySpan<byte> head, out int length, out int frames) =>
    {
        length = 0;
        frames = 0;
        if (!AdtsHeader.TryParse(head, out AdtsHeader h) || h.FrameLength < h.HeaderSize)
        {
            return false;
        }

        length = h.FrameLength;
        frames = 1024 * (h.RawDataBlocks + 1);
        return true;
    };
}
