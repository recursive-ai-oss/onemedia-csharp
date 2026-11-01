using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>The tag system name for values read from transport stream program information.</summary>
public static class MpegTsTagSystems
{
    /// <summary>DVB service information: the service name and provider (SDT) and the present event (EIT).</summary>
    public const string ServiceInformation = "mpegts";

    /// <summary>AVCHD camera data (MDPM) carried in the H.264 SEI: recording time, camera maker and the entries not decoded.</summary>
    public const string CameraData = "mdpm";
}

/// <summary>A flat list of tags read from one table, with the transport packet that began it as its range.</summary>
internal sealed class TsTagContainer(string system, long trackId, ByteRange range, List<NativeTag> tags) : ITagContainer
{
    public string System { get; } = system;

    public long TrackId { get; } = trackId;

    public ByteRange Range { get; } = range;

    public IReadOnlyList<NativeTag> Tags { get; } = tags;
}

/// <summary>A source with nothing in it: tags built from decoded tables carry their text already and never read back.</summary>
internal sealed class EmptyMediaSource : IMediaSource
{
    public long Length => 0;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default) => 0;

    public void Dispose()
    {
    }
}

/// <summary>Service information collected from DVB tables while the front of a stream is read (ETSI EN 300 468 §5.2.3 and §5.2.4).</summary>
internal sealed class TsServiceInfo
{
    public sealed record Service(int Id, string? Provider, string? Name, int Type, long Offset);

    public sealed record Event(int ServiceId, string? Name, string? Text, string? Language, long Offset);

    public List<Service> Services { get; } = [];

    public List<Event> Events { get; } = [];

    public bool HasServiceNames => Services.Count > 0;

    /// <summary>The camera data (MDPM) found in the first access units' SEI, or null.</summary>
    public Avchd.MdpmBlock? Mdpm { get; private set; }

    public bool OnSei(ReadOnlySpan<byte> sei)
    {
        if (Avchd.Mdpm.TryParse(sei, out Avchd.MdpmBlock? block) && block is not null)
        {
            Mdpm = block;
            return true;
        }

        return false;
    }

    public void OnSdt(ReadOnlySpan<byte> s, List<MpegTsDemuxer.ProgramState> programs)
    {
        // §5.2.3: header (8), original_network_id (2), reserved (1), then services: id (2), flags (1), loop length (2).
        int end = s.Length - 4;
        int p = 11;
        while (p + 5 <= end)
        {
            int id = BinaryPrimitives.ReadUInt16BigEndian(s[p..]);
            int loop = BinaryPrimitives.ReadUInt16BigEndian(s[(p + 3)..]) & 0x0FFF;
            p += 5;
            int stop = Math.Min(end, p + loop);
            DescriptorReader r = new(s[p..stop]);
            while (r.TryRead(out int tag, out ReadOnlySpan<byte> body))
            {
                if (tag == 0x48 && body.Length >= 3)
                {
                    // §6.2.33: service_type, provider name, service name, each with its length.
                    int type = body[0];
                    int pl = body[1];
                    if (2 + pl + 1 > body.Length)
                    {
                        continue;
                    }

                    string provider = DvbText.Decode(body.Slice(2, pl));
                    int nl = body[2 + pl];
                    string name = DvbText.Decode(body.Slice(3 + pl, Math.Min(nl, body.Length - 3 - pl)));
                    if (!Services.Exists(x => x.Id == id))
                    {
                        Services.Add(new Service(id, provider, name, type, 0));
                    }

                    foreach (MpegTsDemuxer.ProgramState prog in programs)
                    {
                        if (prog.Number == id)
                        {
                            prog.ServiceName = name;
                            prog.ServiceProvider = provider;
                        }
                    }
                }
            }

            p = stop;
        }
    }

    public void OnEit(ReadOnlySpan<byte> s)
    {
        // §5.2.4: header (8), transport_stream_id (2), original_network_id (2), segment_last_section_number (1), last_table_id (1), then events.
        if (s.Length < 18)
        {
            return;
        }

        int service = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
        int end = s.Length - 4;
        int p = 14;
        while (p + 12 <= end)
        {
            int loop = BinaryPrimitives.ReadUInt16BigEndian(s[(p + 10)..]) & 0x0FFF;
            int start = p + 12;
            int stop = Math.Min(end, start + loop);
            DescriptorReader r = new(s[start..stop]);
            while (r.TryRead(out int tag, out ReadOnlySpan<byte> body))
            {
                if (tag == 0x4D && body.Length >= 5)
                {
                    // §6.2.37: ISO_639_language_code (3), event_name_length, event_name, text_length, text.
                    string lang = global::System.Text.Encoding.ASCII.GetString(body[..3]);
                    int nl = body[3];
                    if (4 + nl + 1 > body.Length)
                    {
                        continue;
                    }

                    string name = DvbText.Decode(body.Slice(4, nl));
                    int tl = body[4 + nl];
                    string text = DvbText.Decode(body.Slice(5 + nl, Math.Min(tl, body.Length - 5 - nl)));
                    if (!Events.Exists(x => x.ServiceId == service))
                    {
                        Events.Add(new Event(service, name, text, lang, 0));
                    }
                }
            }

            p = stop;
        }
    }
}

/// <summary>Builds the metadata view of a transport stream: DVB service and event names, plus AVCHD data when present.</summary>
internal static class TsMetadata
{
    public static MediaMetadata Build(MpegTsDemuxer demuxer, TsServiceInfo info, CancellationToken cancellationToken)
    {
        DefectLog defects = new();
        TagIo io = new(new EmptyMediaSource(), MediaLimits.Default, defects);
        List<ITagContainer> containers = [];
        List<NativeTag> tags = [];
        foreach (MpegTsDemuxer.ProgramState p in demuxer.ProgramStates)
        {
            // Names read from reassembled sections have no single byte range; the tag's range is empty and its text is already decoded.
            Add(tags, io, "program_number", p.Number.ToString(CultureInfo.InvariantCulture), p.Number);
            if (p.ServiceName is { Length: > 0 } n)
            {
                Add(tags, io, "service_name", n, p.Number);
            }

            if (p.ServiceProvider is { Length: > 0 } pr)
            {
                Add(tags, io, "service_provider", pr, p.Number);
            }
        }

        foreach (TsServiceInfo.Event e in info.Events)
        {
            if (e.Name is { Length: > 0 })
            {
                Add(tags, io, "event_name", e.Name, e.ServiceId);
            }

            if (e.Text is { Length: > 0 })
            {
                Add(tags, io, "event_text", e.Text, e.ServiceId);
            }
        }

        if (demuxer.TransportStreamId >= 0)
        {
            Add(tags, io, "transport_stream_id", demuxer.TransportStreamId.ToString(CultureInfo.InvariantCulture), 0);
        }

        containers.Add(new TsTagContainer(MpegTsTagSystems.ServiceInformation, 0, ByteRange.None, tags));
        FactsBuilder facts = new(defects);
        if (demuxer.Duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(demuxer.Duration, "container", demuxer.Duration.ToString(), "last presentation time read from the end of the stream minus the first one"));
        }

        foreach (MediaTrack t in demuxer.Tracks)
        {
            if (t.Video is { Width: > 0 } v)
            {
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "video parameters", $"{v.Width}x{v.Height}", "coded size from the first sequence parameter set or sequence header"));
                break;
            }
        }

        AddMdpm(info.Mdpm, containers, facts, io);
        return new MediaMetadata(containers, [], facts.Build(), defects);
    }

    // MDPM entries are named after ExifTool's published H264 tag list; entries without a decoded meaning are kept as raw hexadecimal.
    private static void AddMdpm(Avchd.MdpmBlock? block, List<ITagContainer> containers, FactsBuilder facts, TagIo io)
    {
        if (block is null)
        {
            return;
        }

        List<NativeTag> tags = [];
        DateTime? when = block.RecordingTime;
        string? make = block.Make;
        if (when is { } w)
        {
            string text = w.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            AddTo(tags, io, MpegTsTagSystems.CameraData, "DateTimeOriginal", text);
            facts.CaptureTime.Add(35, new FactCandidate<MediaDateTime>(new MediaDateTime(w, null, TimeZoneCertainty.Unknown), "mdpm:DateTimeOriginal", text,
                "camera clock from MDPM entries 0x18 and 0x19; the zone flag byte is not decoded, so the zone is unknown"));
        }

        if (make is not null)
        {
            AddTo(tags, io, MpegTsTagSystems.CameraData, "Make", make);
            facts.Make.Add(0, new FactCandidate<string>(make, "mdpm:Make", make, "maker code of MDPM entry 0xE0"));
        }

        foreach (Avchd.MdpmEntry e in block.Entries)
        {
            AddTo(tags, io, MpegTsTagSystems.CameraData, "raw_" + e.Tag.ToString("X2", CultureInfo.InvariantCulture), e.Data.ToString("X8", CultureInfo.InvariantCulture));
        }

        containers.Add(new TsTagContainer(MpegTsTagSystems.CameraData, 0, ByteRange.None, tags));
    }

    private static void AddTo(List<NativeTag> tags, TagIo io, string system, string key, string text)
    {
        NativeTag t = new(io, system, key, 0, TagValueKind.Text, 0, 0, null, ByteRange.None, ByteRange.None);
        t.SetText(text);
        tags.Add(t);
    }

    private static void Add(List<NativeTag> tags, TagIo io, string key, string text, long trackId)
    {
        NativeTag t = new(io, MpegTsTagSystems.ServiceInformation, key, 0, TagValueKind.Text, 0, 0, null, ByteRange.None, ByteRange.None)
        {
        };
        t.SetText(text);
        tags.Add(t);
    }
}
