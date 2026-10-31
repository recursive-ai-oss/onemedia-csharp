using System.Buffers;
using System.Text;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.H264;
using RecursiveAI.OneMedia.Codecs.Hevc;

namespace RecursiveAI.OneMedia.Tests.Codecs;

/// <summary>
/// Repackaging checks against ffmpeg run as a black box: length-prefixed to Annex B and back, decoder configuration records built
/// from Annex B, ADTS to raw plus AudioSpecificConfig, and Matroska/ISO configuration mapping. They use the streams from
/// <c>tools/codecs/make-streams.sh</c> (<c>ONEMEDIA_CODEC_STREAMS</c>) and the ffmpeg named by <c>ONEMEDIA_FFMPEG</c>.
/// </summary>
public sealed class CodecRepackagingTests
{
    private static string? StreamsDir => Environment.GetEnvironmentVariable("ONEMEDIA_CODEC_STREAMS");

    private static string Ffmpeg => Environment.GetEnvironmentVariable("ONEMEDIA_FFMPEG") ?? "ffmpeg";

    private static string Work => Path.Combine(Path.GetTempPath(), "onemedia-codec-tests");

    private static string Report(string name, string text)
    {
        string? dir = Environment.GetEnvironmentVariable("ONEMEDIA_CODEC_REPORT_DIR");
        if (dir is not null)
        {
            File.WriteAllText(Path.Combine(dir, name), text);
        }

        return text;
    }

    private static void RunFfmpeg(string args) => CodecFieldDifferentialTests.RunTool(Ffmpeg, "-v error -y " + args);

    private static IEnumerable<string> Files(string pattern) =>
        StreamsDir is null ? [] : Directory.GetFiles(StreamsDir, pattern).OrderBy(p => p, StringComparer.Ordinal);

    [Fact]
    public void LengthPrefixedToAnnexBEqualsFfmpegBitstreamFilter()
    {
        if (StreamsDir is null)
        {
            return;
        }

        Directory.CreateDirectory(Work);
        StringBuilder log = new();
        int checkedFiles = 0, samples = 0, problems = 0;
        foreach (string path in Files("h264_*.mp4").Concat(Files("hevc_*.mp4")))
        {
            bool hevc = Path.GetFileName(path).StartsWith("hevc", StringComparison.Ordinal);
            string expectedPath = Path.Combine(Work, Path.GetFileNameWithoutExtension(path) + (hevc ? ".hevc" : ".h264"));
            RunFfmpeg($"-i \"{path}\" -c copy -bsf:v {(hevc ? "hevc" : "h264")}_mp4toannexb -f {(hevc ? "hevc" : "h264")} \"{expectedPath}\"");
            byte[] expected = File.ReadAllBytes(expectedPath);
            byte[] file = File.ReadAllBytes(path);
            MiniContainers.Mp4Track track = MiniContainers.ReadMp4(file)!;
            List<byte[]> list = MiniContainers.ReadMp4Samples(file);
            NalCodec codec = hevc ? NalCodec.Hevc : NalCodec.H264;
            int lengthSize;
            byte[] sets;
            if (hevc)
            {
                Assert.True(HevcDecoderConfigurationRecord.TryParse(track.Config, out HevcDecoderConfigurationRecord? h));
                lengthSize = h!.LengthSize;
                sets = NalRepackager.ParameterSetsToAnnexB(h.Arrays.SelectMany(a => a.Units).ToList());
            }
            else
            {
                Assert.True(AvcDecoderConfigurationRecord.TryParse(track.Config, out AvcDecoderConfigurationRecord? a));
                lengthSize = a!.LengthSize;
                sets = NalRepackager.ParameterSetsToAnnexB(a.SequenceParameterSets.Concat(a.PictureParameterSets).ToList());
            }

            using MemoryStream ours = new();
            byte[] buffer = new byte[1 << 20];
            foreach (byte[] sample in list)
            {
                OperationStatus st = NalRepackager.LengthPrefixedToAnnexB(codec, sample, lengthSize, buffer, out int written, new AnnexBOptions(sets));
                Assert.Equal(OperationStatus.Done, st);
                ours.Write(buffer, 0, written);

                // and back: without insertion the framing must round-trip exactly, whatever the sample holds
                Assert.Equal(OperationStatus.Done, NalRepackager.LengthPrefixedToAnnexB(codec, sample, lengthSize, buffer.AsSpan(written), out int plain));
                st = NalRepackager.AnnexBToLengthPrefixed(buffer.AsSpan(written, plain), lengthSize, buffer.AsSpan(written + plain), out int back);
                Assert.Equal(OperationStatus.Done, st);
                Assert.True(buffer.AsSpan(written + plain, back).SequenceEqual(sample), $"{path}: round trip of sample {samples}");
                samples++;
            }

            checkedFiles++;
            bool same = ours.ToArray().AsSpan().SequenceEqual(expected);
            if (!same)
            {
                problems++;
            }

            log.Append(CultureInfoLine($"{Path.GetFileName(path)}: samples={list.Count} ours={ours.Length} ffmpeg={expected.Length} equal={same}"));
        }

        string text = Report("annexb.txt", $"files={checkedFiles} samples={samples} problems={problems}\n{log}");
        Assert.True(problems == 0, text);
    }

    private static string CultureInfoLine(string s) => s + "\n";

    [Fact]
    public void DecoderConfigurationBuiltFromAnnexBEqualsTheOneInTheMp4()
    {
        if (StreamsDir is null)
        {
            return;
        }

        Directory.CreateDirectory(Work);
        StringBuilder log = new();
        int checkedFiles = 0, problems = 0;
        foreach (string path in Files("h264_*.mp4").Concat(Files("hevc_*.mp4")))
        {
            bool hevc = Path.GetFileName(path).StartsWith("hevc", StringComparison.Ordinal);
            string annexB = Path.Combine(Work, Path.GetFileNameWithoutExtension(path) + (hevc ? ".hevc" : ".h264"));
            if (!File.Exists(annexB))
            {
                RunFfmpeg($"-i \"{path}\" -c copy -bsf:v {(hevc ? "hevc" : "h264")}_mp4toannexb -f {(hevc ? "hevc" : "h264")} \"{annexB}\"");
            }

            byte[] stream = File.ReadAllBytes(annexB);
            MiniContainers.Mp4Track track = MiniContainers.ReadMp4(File.ReadAllBytes(path))!;
            ArrayBufferWriter<byte> w = new();
            if (hevc)
            {
                // ffmpeg stores array_completeness 0 for every array; the specification asks for 1 when the arrays are complete.
                Assert.True(HevcDecoderConfigurationRecord.TryCreateFromAnnexB(stream, out HevcDecoderConfigurationRecord? rec, arrayCompleteness: false));
                rec!.Write(w);
            }
            else
            {
                Assert.True(AvcDecoderConfigurationRecord.TryCreateFromAnnexB(stream, out AvcDecoderConfigurationRecord? rec));
                rec!.Write(w);
            }

            bool same = w.WrittenSpan.SequenceEqual(track.Config);
            checkedFiles++;
            if (!same)
            {
                problems++;
                log.Append(CultureInfoLine($"{Path.GetFileName(path)}: ours={Convert.ToHexString(w.WrittenSpan)}\n    mp4 ={Convert.ToHexString(track.Config)}"));
            }
            else
            {
                log.Append(CultureInfoLine($"{Path.GetFileName(path)}: equal ({track.Config.Length} bytes)"));
            }
        }

        string text = Report("config-from-annexb.txt", $"files={checkedFiles} problems={problems}\n{log}");
        Assert.True(problems == 0, text);
    }

    [Fact]
    public void ConfigurationRecordsRewriteToTheSameBytes()
    {
        if (StreamsDir is null)
        {
            return;
        }

        int checkedRecords = 0;
        List<string> problems = [];
        foreach (string path in Files("*.mp4"))
        {
            MiniContainers.Mp4Track t = MiniContainers.ReadMp4(File.ReadAllBytes(path))!;
            ArrayBufferWriter<byte> w = new();
            bool parsed = true;
            switch (t.ConfigType)
            {
                case "avcC":
                    parsed = AvcDecoderConfigurationRecord.TryParse(t.Config, out AvcDecoderConfigurationRecord? avc);
                    avc?.Write(w);
                    break;
                case "hvcC":
                    parsed = HevcDecoderConfigurationRecord.TryParse(t.Config, out HevcDecoderConfigurationRecord? hevc);
                    hevc?.Write(w);
                    break;
                case "av1C":
                    parsed = RecursiveAI.OneMedia.Codecs.Av1.Av1CodecConfigurationRecord.TryParse(t.Config, out RecursiveAI.OneMedia.Codecs.Av1.Av1CodecConfigurationRecord? av1);
                    av1?.Write(w);
                    break;
                case "vpcC":
                    parsed = RecursiveAI.OneMedia.Codecs.Vpx.VpCodecConfigurationRecord.TryParse(t.Config, out RecursiveAI.OneMedia.Codecs.Vpx.VpCodecConfigurationRecord? vp);
                    vp?.Write(w);
                    break;
                case "dOps":
                    parsed = RecursiveAI.OneMedia.Codecs.Opus.OpusHead.TryParseDOps(t.Config, out RecursiveAI.OneMedia.Codecs.Opus.OpusHead? head);
                    head?.WriteDOps(w);
                    break;
                case "dac3":
                    parsed = RecursiveAI.OneMedia.Codecs.Ac3.Ac3SyncFrame.TryParse(t.FirstSample, out RecursiveAI.OneMedia.Codecs.Ac3.Ac3SyncFrame frame) && frame.TryWriteDac3(w);
                    break;
                case "dec3":
                    parsed = RecursiveAI.OneMedia.Codecs.Ac3.Ac3SyncFrame.TryParse(t.FirstSample, out RecursiveAI.OneMedia.Codecs.Ac3.Ac3SyncFrame eframe) && eframe.TryWriteDec3((eframe.BitRate + 999) / 1000, w);
                    break;
                default:
                    continue;
            }

            checkedRecords++;
            if (!parsed || !w.WrittenSpan.SequenceEqual(t.Config))
            {
                problems.Add($"{Path.GetFileName(path)} {t.ConfigType}: parsed={parsed} ours={Convert.ToHexString(w.WrittenSpan)} file={Convert.ToHexString(t.Config)}");
            }
        }

        string text = Report("config-roundtrip.txt", $"records={checkedRecords} problems={problems.Count}\n{string.Join('\n', problems)}\n");
        Assert.True(problems.Count == 0, text);
    }

    [Fact]
    public void AdtsToRawAndConfigEqualsFfmpegCopyToMp4()
    {
        if (StreamsDir is null)
        {
            return;
        }

        Directory.CreateDirectory(Work);
        StringBuilder log = new();
        int frames = 0, problems = 0, checkedFiles = 0;
        foreach (string path in Files("*.aac"))
        {
            string mp4 = Path.Combine(Work, Path.GetFileNameWithoutExtension(path) + "-from-adts.mp4");
            RunFfmpeg($"-i \"{path}\" -c copy \"{mp4}\"");
            byte[] mp4File = File.ReadAllBytes(mp4);
            MiniContainers.Mp4Track track = MiniContainers.ReadMp4(mp4File)!;
            Assert.True(EsDescriptor.TryParse(track.Config, out EsDescriptor? es));
            List<byte[]> expected = MiniContainers.ReadMp4Samples(mp4File);

            byte[] adts = File.ReadAllBytes(path);
            ArrayBufferWriter<byte> asc = new();
            Assert.True(AdtsRepackager.TryCreateConfig(adts, asc));
            bool ascEqual = asc.WrittenSpan.SequenceEqual(es!.DecoderSpecificInfo.Span);
            Assert.True(AudioSpecificConfig.TryParse(asc.WrittenSpan, out AudioSpecificConfig config));
            int pos = 0, i = 0;
            bool rawEqual = true, wrapEqual = true;
            byte[] wrap = new byte[9000];
            while (pos < adts.Length)
            {
                Assert.True(AdtsHeader.TryParse(adts.AsSpan(pos), out AdtsHeader h));
                ReadOnlySpan<byte> frame = adts.AsSpan(pos, h.FrameLength);
                Assert.True(AdtsRepackager.TryGetRawData(frame, out ReadOnlySpan<byte> raw));
                rawEqual &= i < expected.Count && raw.SequenceEqual(expected[i]);
                Assert.Equal(OperationStatus.Done, AdtsRepackager.WrapRaw(config, raw, wrap, out int n));
                wrapEqual &= wrap.AsSpan(0, n).SequenceEqual(frame);
                pos += h.FrameLength;
                i++;
            }

            frames += i;
            checkedFiles++;
            bool ok = ascEqual && rawEqual && i == expected.Count;
            if (!ok)
            {
                problems++;
            }

            log.Append(CultureInfoLine($"{Path.GetFileName(path)}: frames={i} mp4samples={expected.Count} asc={Convert.ToHexString(asc.WrittenSpan)} ascEqual={ascEqual} rawEqual={rawEqual} rewrapEqualToAdts={wrapEqual}"));
        }

        string text = Report("adts.txt", $"files={checkedFiles} frames={frames} problems={problems}\n{log}");
        Assert.True(problems == 0, text);
    }

    [Fact]
    public void ConfigurationMappingEqualsFfmpegRemux()
    {
        if (StreamsDir is null)
        {
            return;
        }

        Directory.CreateDirectory(Work);
        StringBuilder log = new();
        int checkedPairs = 0, problems = 0;
        (string Prefix, CodecId Codec)[] cases =
        [
            ("h264_high", CodecId.H264), ("hevc_main", CodecId.Hevc), ("av1_aom8", CodecId.Av1), ("aac_lc", CodecId.Aac), ("aac_51", CodecId.Aac),
            ("opus_stereo", CodecId.Opus), ("opus_51", CodecId.Opus), ("flac16", CodecId.Flac), ("flac24", CodecId.Flac), ("ac3", CodecId.Ac3),
            ("eac3", CodecId.Eac3), ("mp3_cbr", CodecId.Mp3), ("vp9_p0", CodecId.Vp9),
        ];
        foreach ((string prefix, CodecId codec) in cases)
        {
            string mkv = Path.Combine(StreamsDir, prefix + ".mkv");
            string mp4 = Path.Combine(StreamsDir, prefix + ".mp4");

            // Matroska to ISO: map our own reading of CodecPrivate and compare with the box ffmpeg writes when remuxing.
            string remuxed = Path.Combine(Work, prefix + "-from-mkv.mp4");
            RunFfmpeg($"-i \"{mkv}\" -c copy \"{remuxed}\"");
            MiniContainers.MkvTrack mk = MiniContainers.ReadMkv(File.ReadAllBytes(mkv))!;
            MiniContainers.Mp4Track theirIso = MiniContainers.ReadMp4(File.ReadAllBytes(remuxed))!;
            ArrayBufferWriter<byte> w = new();
            ConfigMapStatus st = CodecConfigMapper.MatroskaToIso(codec, mk.CodecPrivate, mk.FirstFrame, w, out string? box);
            string a = DescribeMapping("mkv->iso", prefix, st, box, w.WrittenSpan, theirIso.ConfigType, theirIso.Config, out bool aOk);

            // ISO to Matroska
            string remuxedMkv = Path.Combine(Work, prefix + "-from-mp4.mkv");
            RunFfmpeg($"-i \"{mp4}\" -c copy \"{remuxedMkv}\"");
            MiniContainers.Mp4Track iso = MiniContainers.ReadMp4(File.ReadAllBytes(mp4))!;
            MiniContainers.MkvTrack theirMkv = MiniContainers.ReadMkv(File.ReadAllBytes(remuxedMkv))!;
            ArrayBufferWriter<byte> w2 = new();
            ConfigMapStatus st2 = iso.ConfigType is null ? ConfigMapStatus.NoConfiguration : CodecConfigMapper.IsoToMatroska(codec, iso.Config, w2);
            string b = DescribeMapping("iso->mkv", prefix, st2, null, w2.WrittenSpan, null, theirMkv.CodecPrivate, out bool bOk);
            if (!bOk && codec == CodecId.Vp9 && theirMkv.CodecPrivate.Length == 0 && st2 == ConfigMapStatus.Done)
            {
                // ffmpeg writes no CodecPrivate for VP9 in Matroska; the WebM feature list we write is optional metadata.
                bOk = true;
                b += "    (explained: the reference writer omits the VP9 CodecPrivate; ours is the WebM feature list)\n";
            }


            checkedPairs += 2;
            problems += (aOk ? 0 : 1) + (bOk ? 0 : 1);
            log.Append(a).Append(b);
        }

        string text = Report("config-mapping.txt", $"pairs={checkedPairs} problems={problems}\n{log}");
        Assert.True(problems == 0, text);
    }

    private static bool EsdsEquivalent(ReadOnlySpan<byte> ours, byte[] theirs) =>
        EsDescriptor.TryParse(ours.ToArray(), out EsDescriptor? a) && EsDescriptor.TryParse(theirs, out EsDescriptor? b)
        && a!.ObjectTypeIndication == b!.ObjectTypeIndication && a.StreamType == b.StreamType && a.DecoderSpecificInfo.Span.SequenceEqual(b.DecoderSpecificInfo.Span);

    private static string DescribeMapping(string dir, string name, ConfigMapStatus status, string? boxType, ReadOnlySpan<byte> ours, string? theirBoxType, byte[] theirs, out bool ok)
    {
        bool typeOk = boxType is null || theirBoxType is null || boxType == theirBoxType;
        bool bytesOk = status switch
        {
            // esds: the descriptor sizes may be written as one byte or as four (observed: four, with ES_ID 1), and ffmpeg adds the bit rates it knows;
            // what must agree is the object type, stream type and the decoder-specific info.
            ConfigMapStatus.Done when boxType == "esds" => EsdsEquivalent(ours, theirs),
            ConfigMapStatus.Done => ours.SequenceEqual(theirs),
            ConfigMapStatus.NoConfiguration => theirs.Length == 0 || theirBoxType is null,
            _ => false,
        };
        ok = typeOk && bytesOk;
        return CultureInfoLine($"{dir} {name}: status={status} box={boxType}/{theirBoxType} ours={ours.Length} bytes theirs={theirs.Length} bytes equal={ok}" + (ok ? "" : $"\n    ours  ={Convert.ToHexString(ours)}\n    theirs={Convert.ToHexString(theirs)}"));
    }
}
