using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.MpegTs;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Tests.TsMux;

public class MpegTsMuxerTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "isobmff", name);

    private sealed record Pkt(int Track, MediaTime Pts, MediaTime Dts, bool Key, byte[] Data);

    private static List<Pkt> ReadAll(byte[] bytes, out IReadOnlyList<MediaTrack> tracks)
    {
        using MediaFile f = Media.Open(bytes);
        tracks = f.Tracks;
        List<Pkt> list = [];
        IDemuxer d = f.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            list.Add(new Pkt(v.Info.TrackIndex, v.Info.Pts, v.Info.Dts, v.Info.IsKeyframe, v.Data.ToArray()));
        }

        return list;
    }

    private static byte[] Remux(string name, RemuxFormat format, out RemuxReport report)
    {
        using MediaFile input = Media.Open(Data(name));
        using MemoryStream ms = new();
        report = Remuxer.Remux(input, ms, format);
        return ms.ToArray();
    }

    // The NAL units of an Annex B access unit or length-prefixed sample, without delimiters and parameter sets.
    private static List<byte[]> Nals(byte[] data, bool annexB)
    {
        List<byte[]> list = [];
        if (annexB)
        {
            AnnexBReader r = new(data);
            while (r.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
            {
                Add(nal);
            }
        }
        else
        {
            int pos = 0;
            while (pos + 4 <= data.Length)
            {
                int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
                Add(data.AsSpan(pos + 4, len));
                pos += 4 + len;
            }
        }

        return list;

        void Add(ReadOnlySpan<byte> nal)
        {
            int type = nal[0] & 0x1F;
            if (nal.Length > 0 && type is not (7 or 8 or 9))
            {
                list.Add(nal.ToArray());
            }
        }
    }

    // Structure of a transport stream: sync bytes, continuity (§2.4.3.3), PCR spacing and PAT/PMT with valid CRC; returns the PCR gaps in ms.
    private static void CheckStructure(byte[] ts, int size, out int pcrs, out double maxPcrGapMs)
    {
        Assert.Equal(0, ts.Length % size);
        int prefix = size - 188;
        Dictionary<int, int> cc = [];
        long last = -1;
        pcrs = 0;
        maxPcrGapMs = 0;
        bool pat = false, pmt = false;
        for (int i = 0; i < ts.Length / size; i++)
        {
            ReadOnlySpan<byte> p = ts.AsSpan((i * size) + prefix, 188);
            Assert.Equal(0x47, p[0]);
            int pid = ((p[1] & 0x1F) << 8) | p[2];
            int afc = (p[3] >> 4) & 3;
            int c = p[3] & 15;
            if (cc.TryGetValue(pid, out int prev))
            {
                int expected = (afc & 1) != 0 ? (prev + 1) & 15 : prev;
                Assert.True(c == expected, $"continuity error on PID {pid} at packet {i}");
            }

            cc[pid] = c;
            int at = 4;
            if ((afc & 2) != 0)
            {
                int al = p[4];
                Assert.True(al <= 183);
                if (al >= 7 && (p[5] & 0x10) != 0)
                {
                    long v = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        v = (v << 8) | p[6 + k];
                    }

                    long pcr = (v >> 15) * 300 + (v & 0x1FF);
                    if (last >= 0)
                    {
                        Assert.True(pcr >= last, "PCR went backwards");
                        maxPcrGapMs = Math.Max(maxPcrGapMs, (pcr - last) / 27000.0);
                    }

                    last = pcr;
                    pcrs++;
                }

                at = 5 + al;
            }

            if (pid is 0 or 0x1000 && (afc & 1) != 0 && (p[1] & 0x40) != 0)
            {
                int ptr = p[at];
                ReadOnlySpan<byte> sec = p[(at + 1 + ptr)..];
                int len = ((sec[1] & 0xF) << 8) | sec[2];
                Assert.Equal(0u, Mpeg2Crc.Compute(sec[..(3 + len)]));
                pat |= pid == 0;
                pmt |= pid == 0x1000;
            }
        }

        Assert.True(pat && pmt);
    }

    [Theory]
    [InlineData("bframes_aac.mp4", RemuxFormat.MpegTs, 188)]
    [InlineData("bframes_aac.mp4", RemuxFormat.M2ts, 192)]
    public void Mp4ToTsReadsBackWithTheSamePackets(string name, RemuxFormat format, int size)
    {
        byte[] ts = Remux(name, format, out RemuxReport report);
        Assert.Equal(ts.Length, report.Mux.BytesWritten);
        CheckStructure(ts, size, out int pcrs, out double gap);
        Assert.True(pcrs > 0 && gap <= 100.0, $"PCR gap {gap} ms");

        using MediaFile source = Media.Open(Data(name));
        List<Pkt> input = [];
        IDemuxer d = source.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            input.Add(new Pkt(v.Info.TrackIndex, v.Info.Pts, v.Info.Dts, v.Info.IsKeyframe, v.Data.ToArray()));
        }

        List<Pkt> output = ReadAll(ts, out IReadOnlyList<MediaTrack> tracks);
        Assert.Equal(source.Tracks.Count, tracks.Count);
        for (int t = 0; t < tracks.Count; t++)
        {
            List<Pkt> a = input.Where(x => x.Track == t).ToList();
            List<Pkt> b = output.Where(x => x.Track == t).ToList();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Key, b[i].Key);
                if (source.Tracks[t].Kind == TrackKind.Video)
                {
                    Assert.Equal(Nals(a[i].Data, annexB: false).Select(Convert.ToHexString), Nals(b[i].Data, annexB: true).Select(Convert.ToHexString));
                }
                else
                {
                    // ADTS header (seven bytes, no CRC) in front of the raw access unit.
                    Assert.Equal(a[i].Data, b[i].Data.AsSpan(7).ToArray());
                }
            }
        }

        // Presentation order and spacing survive: differences between neighbours in a track are the same, in 90 kHz ticks.
        List<Pkt> v0 = input.Where(x => x.Track == 0).ToList();
        List<Pkt> v1 = output.Where(x => x.Track == 0).ToList();
        for (int i = 1; i < v0.Count; i++)
        {
            long din = v0[i].Dts.Rescale(90000, MediaRounding.NearestAwayFromZero).Ticks - v0[i - 1].Dts.Rescale(90000, MediaRounding.NearestAwayFromZero).Ticks;
            long dout = v1[i].Dts.Rescale(90000, MediaRounding.NearestAwayFromZero).Ticks - v1[i - 1].Dts.Rescale(90000, MediaRounding.NearestAwayFromZero).Ticks;
            Assert.Equal(din, dout);
        }
    }

    [Fact]
    public void RemuxReportsWhatTheTransportStreamCannotHold()
    {
        _ = Remux("bframes_aac.mp4", RemuxFormat.MpegTs, out RemuxReport report);
        Assert.Contains(report.Losses, l => l.Kind == LossKind.Disposition);
        Assert.DoesNotContain(report.Tracks, t => t.OutputTrack < 0);
        Assert.Equal("none", report.Tracks[0].Repackaging);
    }

    [Fact]
    public void SegmentsStartWithPatAndPmtAndCarryTheCountersOn()
    {
        using MediaFile source = Media.Open(Data("bframes_aac.mp4"));
        List<MemoryStream> parts = [new()];
        using MpegTsMuxer mux = new(parts[0]);
        foreach (MediaTrack t in source.Tracks)
        {
            mux.AddTrack(t);
        }

        IDemuxer d = source.Demuxer!;
        int keyframes = 0;
        while (d.TryReadPacket(out PacketView v))
        {
            if (mux.CanStartSegmentAt(v.Info) && keyframes++ > 0 && keyframes % 2 == 1)
            {
                MemoryStream next = new();
                parts.Add(next);
                mux.StartSegment(next);
            }

            mux.WritePacket(v);
        }

        mux.Finish();
        Assert.True(parts.Count >= 2, "the fixture has too few keyframes");
        foreach (MemoryStream part in parts)
        {
            byte[] b = part.ToArray();
            Assert.Equal(0, b.Length % 188);
            Assert.Equal(0, ((b[1] & 0x1F) << 8) | b[2]); // PAT first
            Assert.Equal(0x1000, ((b[188 + 1] & 0x1F) << 8) | b[188 + 2]); // then PMT
        }

        // Joined, the segments are one stream with unbroken counters.
        byte[] all = parts.SelectMany(p => p.ToArray()).ToArray();
        CheckStructure(all, 188, out _, out double gap);
        Assert.True(gap <= 100.0);
        Assert.Equal(parts.Count, mux.SegmentIndex);
    }

    private static MediaTrack Video(string codec = "h264", ReadOnlyMemory<byte> config = default, string? format = null) => new()
    {
        Index = 0,
        Kind = TrackKind.Video,
        Codec = new CodecId(codec),
        Timescale = 90000,
        Configuration = new CodecConfiguration(config, format),
    };

    private static PacketInfo Info(int track, long pts, long dts, int size, bool key = true) =>
        new(track, new MediaTime(pts, 90000), new MediaTime(dts, 90000), new MediaTime(3600, 90000), key ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, size);

    [Fact]
    public void RefusalsAreTyped()
    {
        using MpegTsMuxer mux = new(new MemoryStream());
        MuxRefusedException opus = Assert.Throws<MuxRefusedException>(() => mux.AddTrack(new MediaTrack { Index = 0, Kind = TrackKind.Audio, Codec = CodecId.Opus, Timescale = 48000 }));
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, opus.Reason);
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Video("vp9"))).Reason);
        Assert.Equal(MuxRefusalReason.InvalidConfiguration, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Video("h264", new byte[] { 1, 2, 3 }, "avcC"))).Reason);
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Info(0, 0, 0, 4), new byte[4])).Reason);

        int index = mux.AddTrack(Video());
        byte[] annexB = [0, 0, 0, 1, 0x65, 1, 2, 3];
        mux.WritePacket(Info(index, 9000, 9000, annexB.Length), annexB);
        Assert.Equal(MuxRefusalReason.NonMonotonicDts, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Info(index, 8000, 8000, annexB.Length, false), annexB)).Reason);
        Assert.Equal(MuxRefusalReason.InvalidConfiguration, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Info(index, 12600, 12600, 4), new byte[] { 9, 9, 9, 9 })).Reason);
        PacketInfo none = new(index, MediaTime.Unknown, MediaTime.Unknown, default, PacketAttributes.None, ByteRange.None, annexB.Length);
        // After the first packet a packet with no time continues the track; as the first packet it is refused.
        mux.WritePacket(none, annexB);
        using MpegTsMuxer fresh = new(new MemoryStream());
        int f = fresh.AddTrack(Video());
        Assert.Equal(MuxRefusalReason.MissingTimestamp, Assert.Throws<MuxRefusedException>(() => fresh.WritePacket(none with { TrackIndex = f }, annexB)).Reason);
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Info(5, 20000, 20000, 4), annexB)).Reason);
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Video())).Reason);
        MuxReport report = mux.Finish();
        Assert.Equal(2, report.Tracks[0].Packets);
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Info(index, 30000, 30000, 4), annexB)).Reason);
    }

    [Fact]
    public void PacketLimitIsEnforced()
    {
        using MpegTsMuxer mux = new(new MemoryStream(), new MpegTsMuxOptions { Limits = new MediaLimits { MaxPacketBytes = 16 } });
        int t = mux.AddTrack(Video());
        byte[] big = new byte[17];
        Assert.Equal(MuxRefusalReason.LimitExceeded, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Info(t, 0, 0, big.Length), big)).Reason);
    }

    [Fact]
    public void LongGapsGetPcrOnlyPacketsAndEmptyStreamsStillAnnounceTheProgram()
    {
        MemoryStream ms = new();
        using (MpegTsMuxer mux = new(ms))
        {
            int t = mux.AddTrack(Video());
            byte[] au = [0, 0, 0, 1, 0x65, 1, 2, 3];
            mux.WritePacket(Info(t, 0, 0, au.Length), au);
            mux.WritePacket(Info(t, 90000, 90000, au.Length), au); // a one second gap
            mux.Finish();
        }

        CheckStructure(ms.ToArray(), 188, out int pcrs, out double gap);
        Assert.True(gap <= 80.5, $"gap {gap}");
        Assert.True(pcrs >= 12);
        // The tables keep their rhythm through the gap: PAT and PMT go out at PCR-only packets too, so within a gap they repeat about every 160 ms.
        byte[] bytes = ms.ToArray();
        int pats = 0;
        for (int i = 0; i < bytes.Length; i += 188)
        {
            if (bytes[i + 1] == 0x40 && bytes[i + 2] == 0)
            {
                pats++;
            }
        }

        Assert.True(pats >= 6, $"pats {pats}");

        MemoryStream empty = new();
        using (MpegTsMuxer mux = new(empty))
        {
            mux.AddTrack(Video());
            mux.Finish();
        }

        Assert.Equal(2 * 188, empty.Length);
    }

    [Fact]
    public void HostileScriptsNeverEscapeAsUntypedExceptions()
    {
        // The fuzz target's property on seeded random scripts: only typed refusals escape, and the output is whole sync-aligned packets.
        Random random = new(20261102);
        byte[] script = new byte[600];
        for (int round = 0; round < 3000; round++)
        {
            random.NextBytes(script);
            script[0] &= 0x0F;
            RecursiveAI.OneMedia.Fuzz.TsMuxTarget.Script(script.AsSpan(0, 40 + random.Next(560)));
        }
    }
}
