// usage: matroskaoneaudio <file.mkv|mka|webm> <same-audio.ogg|opus|mp4|m4a>
// Compares the audio packets of the first audio track of a Matroska file with the packets OneAudio's Ogg or MP4 reader gives for the
// same audio remuxed without re-encoding, and the codec configuration and priming each side reports.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Matroska;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: matroskaoneaudio <matroska> <ogg-or-mp4>");
    return 2;
}

using MatroskaDemuxer mkv = MatroskaDemuxer.Open(FileMediaSource.Open(args[0]));
MediaTrack track = mkv.Tracks.First(t => t.Kind == TrackKind.Audio);
List<byte[]> ours = [];
MediaTime lastPadding = default;
while (mkv.TryReadPacket(out PacketView p))
{
    if (p.Info.TrackIndex != track.Index) { continue; }
    ours.Add(p.Data.ToArray());
    if (p.Info.DiscardPadding.Ticks != 0) { lastPadding = p.Info.DiscardPadding; }
}

List<byte[]> headers = [];
List<byte[]> theirs = [];
OneAudio.CodecId codec;
int priming;
byte[] config;
using FileStream fs = File.OpenRead(args[1]);
if (args[1].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || args[1].EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
{
    using OneAudio.Mp4.Mp4PacketSource src = OneAudio.Mp4.Mp4PacketSource.Open(fs);
    codec = src.Codec;
    OneAudio.IPacketSource ips = src;
    priming = ips.PrimingFrames;
    config = ips.CodecConfig.ToArray();
    while (src.TryReadPacket(out OneAudio.PacketView p)) { (p.IsHeader ? headers : theirs).Add(p.Data.ToArray()); }
}
else
{
    using OneAudio.Ogg.OggPacketSource src = OneAudio.Ogg.OggPacketSource.Open(fs);
    codec = src.Codec;
    OneAudio.IPacketSource ips = src;
    priming = ips.PrimingFrames;
    config = ips.CodecConfig.ToArray();
    while (src.TryReadPacket(out OneAudio.PacketView p)) { (p.IsHeader ? headers : theirs).Add(p.Data.ToArray()); }
}

int equal = 0, different = 0;
for (int i = 0; i < Math.Min(ours.Count, theirs.Count); i++)
{
    if (ours[i].AsSpan().SequenceEqual(theirs[i])) { equal++; } else { different++; }
}

string configNote;
byte[] priv = track.Configuration.Data.ToArray();
long ourPriming = track.Edit.Priming.IsKnown && track.Audio is { } au && au.SampleRate > 0 ? track.Edit.Priming.Rescale(au.SampleRate, MediaRounding.NearestAwayFromZero).Ticks : 0;
switch (track.Codec.Name)
{
    case "opus":
        // RFC 7845 §5.1: pre-skip is the 16-bit little-endian value at byte 10 of OpusHead, in 48 kHz samples.
        int preSkip = priv.Length >= 12 ? priv[10] | (priv[11] << 8) : -1;
        configNote = (headers.Count > 0 && priv.AsSpan().SequenceEqual(headers[0]) ? "OpusHead == Ogg header packet 0" : "OpusHead DIFFERS from Ogg header packet 0") + $"; OpusHead pre-skip {preSkip} vs CodecDelay {ourPriming} frames";
        break;
    case "vorbis":
        if (MatroskaCodecPrivate.TrySplitXiphLacing(track.Configuration.Data, out ReadOnlyMemory<byte>[] h) && h.Length == 3 && headers.Count >= 3)
        {
            configNote = "Vorbis headers identification/comment/setup equal Ogg's: " + string.Join("/", Enumerable.Range(0, 3).Select(i => h[i].Span.SequenceEqual(headers[i]).ToString()));
        }
        else { configNote = $"Vorbis CodecPrivate did not split into 3 (OneAudio header packets: {headers.Count})"; }
        break;
    case "flac":
        // Matroska: 'fLaC' plus metadata blocks. Ogg: 0x7F 'FLAC' major minor count 'fLaC' plus the STREAMINFO block in the first packet.
        configNote = $"FLAC CodecPrivate {priv.Length} bytes starts '{System.Text.Encoding.ASCII.GetString(priv, 0, Math.Min(4, priv.Length))}'; OneAudio header packets {headers.Count} (first {(headers.Count > 0 ? headers[0].Length : 0)} bytes); STREAMINFO body in Ogg packet == CodecPrivate STREAMINFO body: {(headers.Count > 0 && priv.Length >= 42 && headers[0].AsSpan().IndexOf(priv.AsSpan(8, 34)) >= 0)}";
        break;
    default:
        configNote = priv.AsSpan().SequenceEqual(config) ? "CodecPrivate == OneAudio CodecConfig" : "CodecPrivate DIFFERS from OneAudio CodecConfig";
        break;
}

Console.WriteLine($"{Path.GetFileName(args[0])} vs {Path.GetFileName(args[1])}: codec {track.Codec}/{codec} packets ours={ours.Count} oneaudio={theirs.Count} bytesEqual={equal} bytesDifferent={different} | {configNote} | priming frames ours={ourPriming} oneaudio={priming} | last DiscardPadding={(lastPadding.Ticks == 0 ? "none" : lastPadding.ToString())}");
return different == 0 && ours.Count == theirs.Count ? 0 : 1;
