using System;
using System.Collections.Generic;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for text tracks: <c>texttrack-tx3g</c> (tx3g and QuickTime text), <c>texttrack-wvtt</c>, <c>texttrack-stpp</c> and <c>texttrack-matroska</c>
/// (S_TEXT/UTF8, S_TEXT/ASS, S_TEXT/WEBVTT and WebM WebVTT). Input: one selector byte, a 16-bit big-endian length and that many bytes of sample entry or
/// CodecPrivate, one byte of length and that many bytes of BlockAdditional, then the packet. The packet is decoded into cues, and the cues are written
/// back in every text-track format and decoded again. Nothing may throw except a limit or format exception.
/// </summary>
internal static class TextTrackTarget
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["texttrack-tx3g"] = d => Run(d, d.Length > 0 && (d[0] & 1) == 0 ? TextTrackFormat.Tx3g : TextTrackFormat.QuickTimeText);
        targets["texttrack-wvtt"] = d => Run(d, TextTrackFormat.WebVttIso);
        targets["texttrack-stpp"] = d => Run(d, TextTrackFormat.XmlSubtitleIso);
        targets["texttrack-matroska"] = d => Run(d, (d.Length > 0 ? d[0] & 3 : 0) switch
        {
            0 => TextTrackFormat.SubRip,
            1 => TextTrackFormat.Ass,
            2 => TextTrackFormat.WebVtt,
            _ => TextTrackFormat.WebmWebVtt,
        });
    }

    private static void Run(ReadOnlySpan<byte> data, TextTrackFormat format)
    {
        if (data.Length < 4)
        {
            return;
        }

        int configLength = Math.Min((data[1] << 8) | data[2], data.Length - 3);
        ReadOnlySpan<byte> config = data.Slice(3, configLength);
        ReadOnlySpan<byte> rest = data[(3 + configLength)..];
        if (rest.Length < 1)
        {
            return;
        }

        int addLength = Math.Min(rest[0], rest.Length - 1);
        ReadOnlySpan<byte> additional = rest.Slice(1, addLength);
        ReadOnlySpan<byte> packet = rest[(1 + addLength)..];
        string tag = format switch
        {
            TextTrackFormat.Tx3g => "tx3g",
            TextTrackFormat.QuickTimeText => "text",
            TextTrackFormat.WebVttIso => "wvtt",
            TextTrackFormat.XmlSubtitleIso => "stpp",
            TextTrackFormat.SubRip => "S_TEXT/UTF8",
            TextTrackFormat.Ass => "S_TEXT/ASS",
            TextTrackFormat.WebVtt => "S_TEXT/WEBVTT",
            _ => "D_WEBVTT/SUBTITLES",
        };
        MediaTrack track = new()
        {
            Index = 0,
            Kind = TrackKind.Subtitle,
            Codec = format == TextTrackFormat.Tx3g ? CodecId.TimedText : new CodecId("text"),
            CodecTag = tag,
            Configuration = new CodecConfiguration(config.ToArray()),
            Timescale = 1000,
        };
        try
        {
            TextTrackDecoder decoder = new(track);
            for (int i = 0; i < 2; i++)
            {
                PacketInfo info = new(0, new MediaTime(i * 1000, 1000), new MediaTime(i * 1000, 1000), new MediaTime(1000, 1000), PacketAttributes.Keyframe, ByteRange.None, packet.Length);
                decoder.Add(info, packet, additional);
            }

            SubtitleDocument doc = decoder.Finish();
            foreach (TextTrackFormat f in new[] { TextTrackFormat.Tx3g, TextTrackFormat.WebVttIso, TextTrackFormat.XmlSubtitleIso, TextTrackFormat.SubRip, TextTrackFormat.Ass, TextTrackFormat.WebVtt, TextTrackFormat.WebmWebVtt })
            {
                TextTrackEncoding enc = TextTrackEncoder.Encode(doc, f);
                TextTrackDecoder back = new(enc.Track);
                foreach (TextTrackPacket p in enc.Packets)
                {
                    back.Add(p.Info, p.Data, p.Additional);
                }

                _ = back.Finish();
            }
        }
        catch (MediaException)
        {
        }
    }
}
