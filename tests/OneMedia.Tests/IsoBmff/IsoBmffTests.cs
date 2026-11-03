using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Tests.IsoBmffSupport;
using static RecursiveAI.OneMedia.Tests.IsoBmffSupport.Mp4Builder;

namespace RecursiveAI.OneMedia.Tests;

internal sealed record Got(int Track, long Pts, long Dts, long Duration, int Size, long Pos, bool Key, bool Discard, byte[] Data);

internal static class IsoBmffTestHelpers
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "isobmff", name);

    public static List<Got> Demux(IDemuxer d)
    {
        List<Got> list = [];
        while (d.TryReadPacket(out PacketView v))
        {
            PacketInfo p = v.Info;
            list.Add(new Got(p.TrackIndex, p.Pts.Ticks, p.Dts.Ticks, p.Duration.Ticks, p.Size, p.SourceRange.Offset, p.IsKeyframe, (p.Flags & PacketAttributes.Discard) != 0, v.Data.ToArray()));
        }

        return list;
    }

    public static List<Got> Demux(byte[] file, out IReadOnlyList<MediaTrack> tracks, out DefectLog defects)
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        tracks = d.Tracks;
        defects = d.Defects;
        return Demux(d);
    }
}

[Collection(AllocationMeasurements.Name)]
public class IsoBmffDemuxTests
{
    [Theory]
    [InlineData("bframes_aac.mp4")]
    [InlineData("bframes_aac.mov")]
    [InlineData("faststart.mp4")]
    [InlineData("empty_edit.mp4")]
    [InlineData("frag.mp4")]
    [InlineData("frag_negcts.mp4")]
    [InlineData("video.3gp")]
    [InlineData("mp3.mp4")]
    public void PacketsMatchTheFfprobeListing(string name)
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
        List<Got> ours = IsoBmffTestHelpers.Demux(file, out IReadOnlyList<MediaTrack> tracks, out DefectLog defects);
        Assert.DoesNotContain(defects, d => d.Severity >= DefectSeverity.Warning);
        string[][] theirs = File.ReadAllLines(IsoBmffTestHelpers.Path(name + ".packets.csv")).Select(l => l.Split(',')).ToArray();
        foreach (MediaTrack t in tracks)
        {
            List<Got> mine = ours.Where(g => g.Track == t.Index).ToList();
            string[][] other = theirs.Where(f => f[0] == t.Index.ToString(CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(other.Length, mine.Count);
            // ffprobe applies the first non-empty edit to timestamps: it subtracts that edit's start and adds leading empty edits.
            long editStart = t.Edit.Edits?.FirstOrDefault(e => !e.IsEmpty).MediaStart.Ticks ?? 0;
            long shift = t.Edit.StartTime.Rescale(t.Timescale, MediaRounding.NearestAwayFromZero).Ticks - editStart;
            bool negativeOffsets = mine.Any(g => g.Pts < g.Dts);
            for (int i = 0; i < mine.Count; i++)
            {
                Got m = mine[i];
                string[] o = other[i];
                Assert.Equal(long.Parse(o[4], CultureInfo.InvariantCulture), m.Size);
                Assert.Equal(long.Parse(o[5], CultureInfo.InvariantCulture), m.Pos);
                Assert.Equal(o[6].Contains('K', StringComparison.Ordinal), m.Key);
                Assert.Equal(o[6].Contains('D', StringComparison.Ordinal), m.Discard);
                if (!(t.Codec == CodecId.Mp3 && i == mine.Count - 1))
                {
                    Assert.Equal(long.Parse(o[3], CultureInfo.InvariantCulture), m.Duration);
                }

                if (!negativeOffsets)
                {
                    Assert.Equal(long.Parse(o[1], CultureInfo.InvariantCulture), m.Pts + shift);
                    Assert.Equal(long.Parse(o[2], CultureInfo.InvariantCulture), m.Dts + shift);
                }
            }
        }
    }

    [Fact]
    public void ConstructedFileIsWalkedExactly()
    {
        TrackSpec video = new()
        {
            Id = 1,
            Timescale = 1000,
            SampleCount = 23,
            SizeOf = i => 50 + (i * 3),
            DurationOf = i => i < 10 ? 40u : 33u,
            CompositionOffsetOf = i => (i % 3) switch { 0 => 80, 1 => -40, _ => 0 },
            CompositionVersion1 = true,
            IsSync = i => i % 10 == 0,
            SamplesPerChunk = 4,
        };
        TrackSpec audio = new()
        {
            Id = 2,
            Handler = "soun",
            Timescale = 44100,
            SampleCount = 31,
            SizeOf = i => 200 + (i % 5),
            DurationOf = _ => 1024,
            SamplesPerChunk = 7,
            CompactSizes = true,
        };
        foreach (bool moovFirst in new[] { true, false })
        {
            Layout layout = Build([video, audio], moovFirst);
            List<Got> got = IsoBmffTestHelpers.Demux(layout.ToArray([video, audio]), out IReadOnlyList<MediaTrack> tracks, out DefectLog defects);
            Assert.Empty(defects);
            Assert.Equal(2, tracks.Count);
            Assert.Equal(CodecId.H264, tracks[0].Codec);
            Assert.Equal(CodecId.Aac, tracks[1].Codec);
            Assert.Equal(64, tracks[0].Video!.Value.Width);
            Assert.Equal(44100, tracks[1].Audio!.Value.SampleRate);
            Assert.Equal(54, got.Count);
            // file order: strictly ascending offsets when each track's chunks are ascending
            Assert.True(got.Zip(got.Skip(1), (a, b) => a.Pos < b.Pos).All(x => x));
            foreach (TrackSpec spec in new[] { video, audio })
            {
                List<Got> mine = got.Where(g => g.Track == (spec.Id == 1 ? 0 : 1)).ToList();
                long dts = 0;
                for (int i = 0; i < spec.SampleCount; i++)
                {
                    Got g = mine[i];
                    Assert.Equal(spec.SizeOf(i), g.Size);
                    Assert.Equal(layout.SampleOffsets[spec.Id == 1 ? 0 : 1][i], g.Pos);
                    Assert.Equal(dts, g.Dts);
                    Assert.Equal(dts + (spec.CompositionOffsetOf?.Invoke(i) ?? 0), g.Pts);
                    Assert.Equal(spec.IsSync?.Invoke(i) ?? true, g.Key);
                    Assert.True(g.Data.All(b => b == PayloadByte(spec.Id, i)));
                    dts += spec.DurationOf(i);
                }
            }
        }
    }

    [Fact]
    public void ConstantSizeTableAndSecondSampleEntryAreRead()
    {
        byte[] entries = Cat(DefaultVideoEntry("avc1", 64, 48), DefaultVideoEntry("hvc1", 128, 96));
        TrackSpec t = new() { SampleCount = 9, SizeOf = _ => 17, ConstantSizeBox = true, SamplesPerChunk = 2, SampleEntries = entries, EntryCount = 2 };
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(Build([t], true).ToArray([t])));
        Assert.Equal(2, d.Movie.Tracks[0].SampleEntries.Count);
        Assert.Equal("hvc1", d.Movie.Tracks[0].SampleEntries[1].Format);
        Assert.Equal(128, d.Movie.Tracks[0].SampleEntries[1].Width);
        Assert.Equal(9, IsoBmffTestHelpers.Demux(d).Count);
        Assert.Equal("avcC", d.Tracks[0].Configuration.Format);
    }

    [Fact]
    public void PacketsReportTheSampleDescriptionTheirChunkUses()
    {
        byte[] entries = Cat(DefaultVideoEntry("avc1", 64, 48), DefaultVideoEntry("hvc1", 128, 96));
        TrackSpec t = new() { SampleCount = 10, SizeOf = _ => 17, SamplesPerChunk = 2, SampleEntries = entries, EntryCount = 2, DescriptionOfChunk = c => c < 2 ? 1 : 2 };
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(Build([t], true).ToArray([t])));
        List<int> seen = [];
        while (d.TryReadPacket(out _))
        {
            seen.Add(d.SampleDescriptionIndex);
        }

        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2, 2, 2], seen);
        Assert.Equal("hvc1", d.Movie.Tracks[0].SampleEntries[seen[^1] - 1].Format);
    }

    [Fact]
    public void PcmFramesStoredOnePerSampleAreJoinedWithinAChunk()
    {
        TrackSpec t = new() { Handler = "soun", Timescale = 44100, SampleCount = 2500, SizeOf = _ => 2, DurationOf = _ => 1, SamplesPerChunk = 1200, SampleEntries = DefaultAudioEntry("sowt", 1, 44100) };
        List<Got> got = IsoBmffTestHelpers.Demux(Build([t], true).ToArray([t]), out IReadOnlyList<MediaTrack> tracks, out _);
        Assert.Equal(CodecId.Pcm, tracks[0].Codec);
        // 1024 frames per packet, cut at the end of each chunk even though the chunks follow each other in the file
        Assert.Equal([2048, 352, 2048, 352, 200], got.Select(g => g.Size).ToArray());
        Assert.Equal([0L, 1024, 1200, 2224, 2400], got.Select(g => g.Dts).ToArray());
        Assert.Equal([1024L, 176, 1024, 176, 100], got.Select(g => g.Duration).ToArray());
    }

    [Fact]
    public void Co64OffsetsBeyondFourGigabytesAreFollowed()
    {
        TrackSpec t = new() { Handler = "soun", SampleCount = 4_300_000, SizeOf = _ => 1000, ConstantSizeBox = true, DurationOf = _ => 1024, SamplesPerChunk = 1000, Co64 = true, Timescale = 44100 };
        Layout l = Build([t], moovFirst: false);
        VirtualFileSource src = new(l.Head, l.MdatPayloadSize, l.Tail);
        Assert.True(src.Length > 4L << 30);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src, new MediaOpenOptions { LeaveOpen = true });
        long count = 0, last = -1;
        while (d.TryReadPacket(out PacketView v))
        {
            Assert.True(v.Info.SourceRange.Offset > last);
            last = v.Info.SourceRange.Offset;
            count++;
        }

        Assert.Equal(4_300_000, count);
        Assert.True(last > uint.MaxValue);
        Assert.Equal(l.SampleOffsets[0][^1], last);
    }

    [Fact]
    public void EditListsGiveStartPrimingAndDiscard()
    {
        TrackSpec a = new()
        {
            Handler = "soun",
            Timescale = 48000,
            SampleCount = 20,
            SizeOf = _ => 40,
            DurationOf = _ => 1024,
            Edits = [(500, -1), (400, 1024)], // 0.5 s empty at movie rate 1000, then media from tick 1024
        };
        List<Got> got = IsoBmffTestHelpers.Demux(Build([a], true).ToArray([a]), out IReadOnlyList<MediaTrack> tracks, out _);
        TrackEditInfo e = tracks[0].Edit;
        Assert.Equal(1024, e.Priming.Rescale(48000, MediaRounding.Floor).Ticks);
        Assert.Equal(new MediaTime(500, 1000), e.StartTime);
        Assert.Equal(2, e.Edits!.Count);
        Assert.True(e.Edits[0].IsEmpty);
        Assert.True(got[0].Discard);
        Assert.False(got[1].Discard);
    }

    [Fact]
    public void RepeatedSyncSampleEntriesDoNotHideLaterOnes()
    {
        TrackSpec v = new() { SampleCount = 30, IsSync = i => i % 3 != 2, RepeatSyncEntries = true, SamplesPerChunk = 4 };
        List<Got> got = IsoBmffTestHelpers.Demux(Build([v], true).ToArray([v]), out _, out _);
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(i % 3 != 2, got[i].Key);
        }
    }

    [Fact]
    public void SeekLandsOnKeyframesAndSkipsToEnd()
    {
        TrackSpec v = new()
        {
            SampleCount = 50,
            DurationOf = _ => 40,
            CompositionOffsetOf = _ => 80,
            IsSync = i => i % 10 == 0,
            SamplesPerChunk = 5,
        };
        byte[] file = Build([v], false).ToArray([v]);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        MediaTime landed = d.Seek(new MediaTime(1000, 1000)); // dts 1000 is sample 25; pts of sample 20 is 880 (<= 1000)
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.True(p.Info.IsKeyframe);
        Assert.Equal(800, p.Info.Dts.Ticks);
        Assert.Equal(880, landed.Ticks);
        landed = d.Seek(new MediaTime(1000, 1000), SeekMode.NextKeyframe);
        Assert.True(d.TryReadPacket(out p));
        Assert.Equal(1200, p.Info.Dts.Ticks);
        d.Seek(new MediaTime(1_000_000, 1000), SeekMode.NextKeyframe);
        Assert.False(d.TryReadPacket(out _));
        d.Seek(MediaTime.Zero);
        Assert.True(d.TryReadPacket(out p));
        Assert.Equal(0, p.Info.Dts.Ticks);
    }

    [Fact]
    public void SeekInAFragmentedFileLandsOnAKeyframe()
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path("frag.mp4"));
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        List<Got> all = IsoBmffTestHelpers.Demux(d);
        int video = 0;
        long target = all.Where(g => g.Track == video && g.Key).Skip(1).First().Pts + 1; // just past the second keyframe
        MediaTime landed = d.Seek(new MediaTime(target, d.Tracks[video].Timescale));
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(video, p.Info.TrackIndex);
        Assert.True(p.Info.IsKeyframe);
        Assert.Equal(target - 1, landed.Ticks);
        Assert.Equal(all.Where(g => g.Track == video && g.Key).Skip(1).First().Pos, p.Info.SourceRange.Offset);
    }

    [Theory]
    [InlineData("frag.mp4")]
    [InlineData("faststart.mp4")]
    public void ForwardOnlyReadingGivesTheSamePackets(string name)
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
        List<Got> seekable;
        using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file)))
        {
            seekable = IsoBmffTestHelpers.Demux(d);
        }

        using IsoBmffDemuxer f = IsoBmffDemuxer.Open(new StreamForwardSource(new MemoryStream(file)));
        Assert.False(f.CanSeek);
        List<Got> forward = IsoBmffTestHelpers.Demux(f);
        Assert.Equal(seekable.Count, forward.Count);
        for (int i = 0; i < forward.Count; i++)
        {
            Assert.Equal(seekable[i] with { Data = [] }, forward[i] with { Data = [] });
            Assert.True(seekable[i].Data.AsSpan().SequenceEqual(forward[i].Data));
        }
    }

    [Fact]
    public void ForwardOnlyRefusesMovieAfterMediaData()
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path("bframes_aac.mp4"));
        Assert.Throws<UnsupportedMediaException>(() => IsoBmffDemuxer.Open(new StreamForwardSource(new MemoryStream(file))));
    }

    [Theory]
    [InlineData("bframes_aac.mp4")]
    [InlineData("frag.mp4")]
    public async Task AsyncReadingGivesTheSamePackets(string name)
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
        List<Got> sync;
        using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file)))
        {
            sync = IsoBmffTestHelpers.Demux(d);
        }

        int requests = 0;
        await using RangedMediaSource ranged = new(file.Length, async (offset, buffer, _) =>
        {
            requests++;
            await Task.Yield();
            int n = (int)Math.Min(buffer.Length, file.Length - offset);
            file.AsMemory((int)offset, n).CopyTo(buffer);
            return n;
        });
        await using IsoBmffAsyncDemuxer a = await IsoBmffAsyncDemuxer.OpenAsync(ranged);
        int i = 0;
        while (await a.ReadPacketAsync())
        {
            PacketView v = a.Current;
            Assert.Equal(sync[i].Pos, v.Info.SourceRange.Offset);
            Assert.Equal(sync[i].Dts, v.Info.Dts.Ticks);
            Assert.True(sync[i].Data.AsSpan().SequenceEqual(v.Data));
            i++;
        }

        Assert.Equal(sync.Count, i);
        Assert.True(requests > 0);
    }

    [Fact]
    public void DisabledTracksProduceNoPackets()
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path("bframes_aac.mp4"));
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        d.SetTrackEnabled(1, false);
        Assert.All(IsoBmffTestHelpers.Demux(d), g => Assert.Equal(0, g.Track));
        using IsoBmffDemuxer f = IsoBmffDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(IsoBmffTestHelpers.Path("frag.mp4"))));
        f.SetTrackEnabled(0, false);
        List<Got> fragGot = IsoBmffTestHelpers.Demux(f);
        Assert.NotEmpty(fragGot);
        Assert.All(fragGot, g => Assert.Equal(1, g.Track));
    }

    [Theory]
    [InlineData("bframes_aac.mp4")]
    [InlineData("frag.mp4")]
    public void PullPathAllocatesNothingPerPacket(string name)
    {
        AllocationGuard.RequireOptimised();
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        long sum = 0;
        while (d.TryReadPacket(out PacketView v))
        {
            sum += v.Data.Length; // first pass: buffers grow
        }

        long allocated = AllocationGuard.Best(() =>
        {
            d.Seek(MediaTime.Zero);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int n = 0;
            while (d.TryReadPacket(out PacketView v))
            {
                sum += v.Data.Length + v.Info.Pts.Ticks;
                n++;
            }

            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(n > 30);
            return used;
        });
        Assert.Equal(0, allocated);
        Assert.NotEqual(0, sum);
    }

    [Fact]
    public void ITunesGaplessTagSetsPrimingAndPadding()
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(IsoBmffTestHelpers.Path("family/aac-itunes-gapless.m4a"))));
        Assert.NotNull(d.Movie.GaplessInfo);
        Assert.True(IsoBmffEngine.TryParseGapless(d.Movie.GaplessInfo!, out long delay, out long padding, out _));
        MediaTrack t = d.Tracks[0];
        int rate = t.Audio!.Value.SampleRate;
        Assert.Equal(delay, t.Edit.Priming.Rescale(rate, MediaRounding.NearestAwayFromZero).Ticks);
        Assert.Equal(padding, t.Edit.EndPadding.Rescale(rate, MediaRounding.NearestAwayFromZero).Ticks);
        Assert.Equal(CodecId.Aac, t.Codec);
        Assert.Equal("esds", t.Configuration.Format);
    }

    [Theory]
    [InlineData("family/amrnb.3gp", "amr-nb", "samr")]
    [InlineData("family/amrwb.3gp", "amr-wb", "sawb")]
    public void AmrInThreeGpHasItsConfiguration(string name, string codec, string tag)
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(IsoBmffTestHelpers.Path(name))));
        Assert.Equal(codec, d.Tracks[0].Codec.Name);
        Assert.Equal(tag, d.Tracks[0].CodecTag);
        if (tag == "samr")
        {
            Assert.Equal("damr", d.Tracks[0].Configuration.Format);
        }
        else
        {
            // Observed: ffmpeg writes this entry without a setup box and MP4Box calls such a file invalid ("Config not present"); no tool writes another box for it.
            Assert.True(d.Tracks[0].Configuration.IsEmpty);
        }

        Assert.Equal(TrackKind.Audio, d.Tracks[0].Kind);
        Assert.Equal(150, IsoBmffTestHelpers.Demux(d).Count);
    }

    [Fact]
    public void AmrWbSetupBoxIsTheSameDamrBoxAsAmrNb()
    {
        // 3GPP TS 26.244: the sample entries samr, sawb and sawp all carry an AMRSpecificBox of type damr.
        byte[] damr = Box("damr", "ABCD"u8.ToArray(), [0x80, 0x00, 0x00, 0x01, 0x00]);
        byte[] entry = Box("sawb", new byte[6], U16(1), new byte[8], U16(1), U16(16), U16(0), U16(0), U32(16000u << 16), damr);
        TrackSpec t = new() { Handler = "soun", Timescale = 16000, SampleCount = 3, SizeOf = _ => 61, DurationOf = _ => 320, SampleEntries = entry };
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(Build([t], true, "3gp5").ToArray([t])));
        Assert.Equal(CodecId.AmrWb, d.Tracks[0].Codec);
        Assert.Equal("damr", d.Tracks[0].Configuration.Format);
        Assert.Equal(9, d.Tracks[0].Configuration.Data.Length);
    }

    [Fact]
    public void OnlyTheFirstEnabledTrackOfAKindIsDefault()
    {
        TrackSpec v = new() { Id = 1 };
        TrackSpec a1 = new() { Id = 2, Handler = "soun" };
        TrackSpec a2 = new() { Id = 3, Handler = "soun" };
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(Build([v, a1, a2], true).ToArray([v, a1, a2])));
        Assert.Equal([true, true, false], d.Tracks.Select(t => (t.Disposition & TrackDisposition.Default) != 0).ToArray());
    }

    [Fact]
    public void FragmentWithoutDecodeTimeContinuesAfterTheMovieSamples()
    {
        // A file whose movie box already holds 5 samples of 40 ticks and is followed by a fragment without tfdt: the fragment starts at tick 200.
        TrackSpec v = new() { Id = 1, SampleCount = 5, SizeOf = _ => 10, DurationOf = _ => 40 };
        byte[] mvex = Box("mvex", Full("trex", 0, 0, U32(1, 1, 0, 0, 0)));
        byte[] head = Build([v], true, "isom", mvex).ToArray([v]);
        byte[] Moof(int dataOffset) => Box("moof", Full("mfhd", 0, 0, U32(1)),
            Box("traf", Full("tfhd", 0, 0x20000, U32(1)), Full("trun", 0, 0x301, U32(2), U32((uint)dataOffset), U32(40, 7, 40, 7))));
        int moofSize = Moof(0).Length;
        byte[] file = Cat(head, Moof(moofSize + 8), Box("mdat", new byte[14]));
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        List<Got> got = IsoBmffTestHelpers.Demux(d);
        Assert.Equal([0L, 40, 80, 120, 160, 200, 240], got.Select(g => g.Dts).ToArray());
        Assert.Equal(7, got[^1].Size);
        d.Seek(MediaTime.Zero);
        Assert.Equal(7, IsoBmffTestHelpers.Demux(d).Count);
        Assert.Equal(240, IsoBmffTestHelpers.Demux(file, out _, out _)[^1].Dts);
    }

    [Fact]
    public void RandomAccessBoxOfAFragmentedFileIsReadAndPointsAtFragments()
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path("frag.mp4"));
        Assert.True(MovieFragmentRandomAccessBox.TryReadTrailer(file.AsSpan(file.Length - 16), file.Length, out long offset, out long size));
        Assert.True(MovieFragmentRandomAccessBox.TryParse(file.AsSpan((int)offset, (int)size), offset, new MediaLimits(), out MovieFragmentRandomAccessBox? mfra));
        Assert.NotEmpty(mfra!.Tracks);
        foreach (TrackFragmentRandomAccessBox t in mfra.Tracks)
        {
            Assert.NotEmpty(t.Entries);
            Assert.All(t.Entries, e => Assert.Equal("moof", System.Text.Encoding.ASCII.GetString(file, (int)e.MoofOffset + 4, 4)));
        }
    }

    [Fact]
    public void SegmentIndexIsParsedAndUsedToFindSubsegments()
    {
        // version 0 sidx: track 1, timescale 1000, earliest time 0, first offset 0, three media references of 100 bytes and 2000 ticks
        byte[] refs = Cat(U32(100, 2000, 0x90000000), U32(100, 2000, 0x90000000), U32(100, 2000, 0x90000000));
        byte[] payload = Cat(U32(0, 1, 1000, 0, 0), U16(0), U16(3), refs);
        Assert.True(SegmentIndexBox.TryParse(payload, 1000, 1044, out SegmentIndexBox? sidx));
        Assert.Equal(3, sidx!.References.Count);
        Assert.Equal(new MediaTime(6000, 1000), sidx.TotalDuration);
        Assert.True(sidx.References[0].StartsWithSap);
        Assert.Equal(1, sidx.References[0].SapType);
        Assert.True(sidx.TryFindSubsegment(new MediaTime(3, 1), out long at, out int n));
        Assert.Equal((1144L, 1), (at, n));
        Assert.False(SegmentIndexBox.TryParse(new byte[5], 0, 0, out _));
    }

    [Fact]
    public void GaplessTagIsParsed()
    {
        Assert.True(IsoBmffEngine.TryParseGapless(" 00000000 00000840 000001C0 0000000000A8E0E0 00000000 00000000", out long delay, out long padding, out long frames));
        Assert.Equal((2112, 448, 11067616), (delay, padding, frames));
        Assert.False(IsoBmffEngine.TryParseGapless("nonsense", out _, out _, out _));
    }

    [Fact]
    public void TruncatedAndDamagedBoxesAreTypedDefects()
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path("faststart.mp4"));
        // cut inside the media data: the movie is intact, so every sample before the cut is returned
        int cut = file.Length - 700;
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file.AsSpan(0, cut).ToArray()));
        List<Got> got = IsoBmffTestHelpers.Demux(d);
        Assert.NotEmpty(got);
        Assert.All(got, g => Assert.True(g.Pos + g.Size <= cut));
        Assert.Contains(d.Defects, x => x.Kind == DefectKind.Truncated);

        // a nested size that overruns its parent
        TrackSpec t = new();
        byte[] built = Build([t], true).ToArray([t]);
        int stsd = IndexOf(built, "stsd") - 4;
        BinaryPrimitives.WriteUInt32BigEndian(built.AsSpan(stsd), 0x7FFFFFFF);
        using IsoBmffDemuxer bad = IsoBmffDemuxer.Open(new MemoryMediaSource(built));
        Assert.Contains(bad.Defects, x => x.Kind == DefectKind.SizeExceedsParent && x.Offset == stsd);
    }

    [Fact]
    public void FilesWithoutAMovieBoxAreRejectedWithATypedException()
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path("bframes_aac.mp4"));
        MediaFormatException e = Assert.Throws<MediaFormatException>(() => IsoBmffDemuxer.Open(new MemoryMediaSource(file.AsSpan(0, 40).ToArray())));
        Assert.Equal(DefectKind.MissingRequired, e.Kind);
        Assert.False(IsoBmffDemuxer.TryOpen(new MemoryMediaSource(file.AsSpan(0, 40).ToArray()), null, out IsoBmffDemuxer? none, out MediaDefect failure));
        Assert.Null(none);
        Assert.Equal(DefectKind.MissingRequired, failure.Kind);
    }

    [Fact]
    public void SweepOfTruncationsAndCorruptionsNeverThrowsUntypedExceptions()
    {
        foreach (string name in new[] { "bframes_aac.mp4", "frag.mp4", "video.3gp", "bframes_aac.mov" })
        {
            byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
            int step = Math.Max(1, file.Length / 600);
            for (int cut = 0; cut < file.Length; cut += step)
            {
                Probe(file.AsSpan(0, cut).ToArray());
            }

            Random rng = new(12345);
            for (int i = 0; i < 400; i++)
            {
                byte[] copy = (byte[])file.Clone();
                int flips = 1 + rng.Next(4);
                for (int k = 0; k < flips; k++)
                {
                    int at = rng.Next(0, Math.Min(copy.Length, 3000));
                    copy[at] = rng.Next(4) switch { 0 => 0, 1 => 0xFF, 2 => (byte)(copy[at] ^ 0x80), _ => (byte)rng.Next(256) };
                }

                Probe(copy);
            }
        }

        static void Probe(byte[] data)
        {
            try
            {
                MediaOpenOptions options = new() { Limits = new MediaLimits { MaxAllocationBytes = 4 << 20 } };
                using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(data), options);
                int n = 0;
                while (d.TryReadPacket(out _) && n++ < 100_000)
                {
                }

                d.Seek(new MediaTime(500, 1000));
                while (d.TryReadPacket(out _) && n++ < 200_000)
                {
                }
            }
            catch (MediaException)
            {
            }
        }
    }

    private static int IndexOf(byte[] data, string type)
    {
        byte[] t = System.Text.Encoding.Latin1.GetBytes(type);
        return data.AsSpan().IndexOf(t);
    }
}

public class BoxReaderTests
{
    [Fact]
    public void SizeFormsAreRead()
    {
        // 32-bit size, 64-bit largesize, size 0 to the end, uuid with user type
        byte[] data = Cat(
            Box("free", new byte[4]),
            Cat(U32(1), "wide"u8.ToArray(), U64(20), new byte[4]),
            Cat(U32(24), "uuid"u8.ToArray(), Enumerable.Range(1, 16).Select(i => (byte)i).ToArray()),
            Cat(U32(0), "mdat"u8.ToArray(), new byte[10]));
        DefectLog log = new();
        BoxTree tree = new(new MemoryMediaSource(data), null, log);
        IReadOnlyList<BoxNode> top = tree.TopLevel;
        Assert.Equal(4, top.Count);
        Assert.Equal(16, top[1].HeaderSize);
        Assert.Equal(4, top[1].PayloadSize);
        Assert.Equal(top[2].Header.UserType, new Guid(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(), bigEndian: true));
        Assert.True(top[3].IsSizeInferred);
        Assert.Equal(10, top[3].PayloadSize);
        Assert.Empty(log);
        Assert.Equal(data.Length, top[3].Header.End);
    }

    [Fact]
    public void SizeBelowHeaderAndOverrunsAreReported()
    {
        byte[] data = Cat(Box("free", new byte[4]), Cat(U32(5), "skip"u8.ToArray()));
        DefectLog log = new();
        _ = new BoxTree(new MemoryMediaSource(data), null, log).TopLevel;
        Assert.Contains(log, d => d.Kind == DefectKind.InvalidSize && d.Offset == 12);

        byte[] over = Cat(U32(100), "free"u8.ToArray(), new byte[8]);
        DefectLog log2 = new();
        IReadOnlyList<BoxNode> top = new BoxTree(new MemoryMediaSource(over), null, log2).TopLevel;
        Assert.Single(top);
        Assert.Equal(8, top[0].PayloadSize);
        Assert.Contains(log2, d => d.Kind == DefectKind.SizeExceedsFile && d.Offset == 0);
    }

    [Fact]
    public void QuickTimeMetaWithoutVersionAndFlagsIsRecognised()
    {
        byte[] iso = Box("meta", U32(0), Box("hdlr", new byte[24]), Box("ilst"));
        byte[] qt = Box("meta", Box("hdlr", new byte[24]), Box("ilst"));
        foreach (byte[] meta in new[] { iso, qt })
        {
            BoxNode node = new BoxTree(new MemoryMediaSource(meta)).TopLevel[0];
            Assert.Equal(2, node.Children.Count);
            Assert.Equal("ilst", node.Children[1].TypeName);
        }
    }

    [Fact]
    public void DeepNestingStopsAtTheLimitWithADefect()
    {
        byte[] box = Box("free");
        for (int i = 0; i < 200; i++)
        {
            box = Box("trak", box);
        }

        DefectLog log = new();
        BoxTree tree = new(new MemoryMediaSource(box), new MediaLimits { MaxStructureDepth = 16 }, log);
        int depth = 0;
        IReadOnlyList<BoxNode> level = tree.TopLevel;
        while (level.Count > 0)
        {
            depth++;
            level = level[0].Children;
        }

        Assert.True(depth <= 16);
        Assert.Contains(log, d => d.Kind == DefectKind.LimitReached);
    }

    [Fact]
    public void TrailingZeroTerminatorIsNotAnError()
    {
        byte[] data = Cat(Box("udta", Box("free", new byte[2]), U32(0)));
        DefectLog log = new();
        BoxNode udta = new BoxTree(new MemoryMediaSource(data), null, log).TopLevel[0];
        Assert.Single(udta.Children);
        Assert.DoesNotContain(log, d => d.Severity >= DefectSeverity.Warning);
    }
}
