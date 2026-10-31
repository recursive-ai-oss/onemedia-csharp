using System;
using System.Collections.Generic;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>A reference tool run as a process on the same file. Reported, never gated.</summary>
/// <param name="Name">Label in the table.</param>
/// <param name="Tool">Executable under the oracle directory (or on PATH).</param>
/// <param name="Args">Arguments for a file path.</param>
internal sealed record NativeTool(string Name, string Tool, Func<string, string[]> Args);

/// <summary>One row of the speed bar: a piece of work, the files it runs on, and who does it.</summary>
internal sealed record Row(string Id, string Stage, string Title, string[] Files, Impl[] Impls, NativeTool[] Natives);

internal static class Rows
{
    // Paths are relative to the corpus directory.
    private static readonly string[] Mp4Files =
    [
        "generated/mp4/h264-aac.mp4",
        "samples/MPEG-4/video.mp4",
        "blender/big_buck_bunny_1080p_h264.mov",
        "generated/bench/frag-bbb-1080p.mp4",
        "generated/large/over4gb-moov-at-end.mp4",
    ];

    // The largest Blender Matroska file in the corpus is 1.2 GB; the over-4 GiB file is generated.
    private static readonly string[] MkvFiles =
    [
        "generated/mkv/h264-aac.mkv",
        "matroska-test-files/test1.mkv",
        "matroska-test-files/test5.mkv",
        "blender/Sintel.2010.1080p.mkv",
        "blender/tears_of_steel_1080p.webm",
        "generated/large/over4gb.mkv",
    ];

    private static NativeTool FfmpegCopy { get; } = new("ffmpeg -c copy -f null", "ffmpeg", f => ["-v", "error", "-nostdin", "-i", f, "-map", "0", "-c", "copy", "-f", "null", "-"]);

    private static NativeTool Ffprobe { get; } = new("ffprobe -show_format -show_streams", "ffprobe", f => ["-v", "error", "-show_format", "-show_streams", "-of", "json", f]);

    private static NativeTool MediaInfo { get; } = new("MediaInfo", "mediainfo", f => [f]);

    private static NativeTool ExifTool { get; } = new("ExifTool", "exiftool", f => ["-a", "-G1", f]);

    public static IReadOnlyList<Row> All { get; } =
    [
        new("mp4-demux", "0.1", "MP4/MOV: read every packet of every track", Mp4Files,
            [new("OneMedia", Role.Ours, Impls.OursDemux), new("SharpMP4", Role.Gate, Impls.SharpMp4Demux)],
            [FfmpegCopy]),
        new("mp4-audio", "0.1", "MP4/MOV: read the audio track's packets", Mp4Files,
            [new("OneMedia", Role.Ours, Impls.OursAudio), new("OneAudio Mp4PacketSource", Role.Gate, Impls.OneAudioMp4)],
            []),
        new("mp4-probe", "0.1", "MP4/MOV: open, track list, duration, tags", Mp4Files,
            [
                new("OneMedia", Role.Ours, Impls.OursProbe),
                new("MetadataExtractor", Role.Gate, Impls.MetadataExtractorProbe),
                new("TagLib#", Role.Gate, Impls.TagLibProbe),
                new("ATL.NET", Role.Gate, Impls.AtlProbe),
            ],
            [Ffprobe, MediaInfo, ExifTool, new("MP4Box -info", "MP4Box", f => ["-info", f])]),
        new("mkv-demux", "0.1", "Matroska/WebM: read every packet", MkvFiles,
            [new("OneMedia", Role.Ours, Impls.OursDemux), new("StefH Matroska", Role.Gate, Impls.StefHMatroskaDemux)],
            [FfmpegCopy, new("mkvextract tracks (first track)", "mkvextract", f => [f, "tracks", "0:/dev/null"])]),
        new("mkv-probe", "0.1", "Matroska/WebM: open, track list, duration", MkvFiles,
            [new("OneMedia", Role.Ours, Impls.OursProbe), new("NEbml element reader", Role.Gate, Impls.NEbmlProbe)],
            [Ffprobe, MediaInfo, ExifTool, new("mkvmerge -J", "mkvmerge", f => ["-J", f]), new("mkvinfo", "mkvinfo", f => [f])]),
    ];
}
