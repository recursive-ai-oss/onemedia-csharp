namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>The packet size a transport stream is written with.</summary>
public enum TsPacketLayout
{
    /// <summary>188-byte transport packets (ISO/IEC 13818-1 §2.4.3), the layout of broadcast and HLS.</summary>
    Ts188 = 188,

    /// <summary>192-byte packets: each 188-byte packet follows a 4-byte <c>TP_extra_header</c> with a 30-bit arrival time stamp
    /// (the M2TS layout of Blu-ray and AVCHD files).</summary>
    M2ts192 = 192,
}

/// <summary>Settings for <see cref="MpegTsMuxer"/>. Every value has a default that gives a conforming single-program stream.</summary>
public sealed record MpegTsMuxOptions
{
    /// <summary>Packet size. Default <see cref="TsPacketLayout.Ts188"/>.</summary>
    public TsPacketLayout Layout { get; init; } = TsPacketLayout.Ts188;

    /// <summary>The program_number of the one program, 1 to 65535. Default 1.</summary>
    public int ProgramNumber { get; init; } = 1;

    /// <summary>The transport_stream_id in the PAT, 0 to 65535. Default 1.</summary>
    public int TransportStreamId { get; init; } = 1;

    /// <summary>PID of the program map table, 32 to 8190. Default 0x1000.</summary>
    public int PmtPid { get; init; } = 0x1000;

    /// <summary>PID given to the first track; later tracks follow in order. 32 to 8190 minus the track count. Default 0x100.</summary>
    public int FirstElementaryPid { get; init; } = 0x100;

    /// <summary>
    /// The longest gap between two program clock references, in milliseconds, 10 to 100 (ISO/IEC 13818-1 §2.7.2 allows 100). When the next
    /// frame is further away than this the muxer inserts PCR-only packets. Default 80, so a 12.5 fps or faster video needs none.
    /// </summary>
    public int PcrIntervalMilliseconds { get; init; } = 80;

    /// <summary>How often PAT and PMT are repeated, in milliseconds of stream time, 10 to 500. They are also written first in every segment. Default 100.</summary>
    public int PsiIntervalMilliseconds { get; init; } = 100;

    /// <summary>
    /// The presentation time the first decode time is moved to. A decoder needs the program clock to be ahead of the first decode time by the
    /// time the first picture takes to arrive, so output times never start at zero. Default 1.4 s (so 0.9 s above the first PCR). A packet
    /// whose time lands before zero (earlier than the first packet by more than this) is refused.
    /// </summary>
    public MediaTime InitialDelay { get; init; } = new(7, 5);

    /// <summary>How far the program clock runs ahead of the decode time of the packet it travels with. Default 0.5 s.</summary>
    public MediaTime PcrLead { get; init; } = new(1, 2);

    /// <summary>
    /// Put an access unit delimiter in front of every H.264, HEVC and VVC access unit that has none, as the transport stream
    /// profiles expect (ITU-T H.222.0 Amd. and Apple HLS authoring rules ask for them). Default true.
    /// </summary>
    public bool InsertAccessUnitDelimiters { get; init; } = true;

    /// <summary>
    /// Put the parameter sets of the track's configuration record in front of every keyframe that carries none, so a decoder can start at any
    /// keyframe (and any segment). Applies to tracks given as <c>avcC</c>/<c>hvcC</c>/<c>vvcC</c> records and to Annex B tracks whose first
    /// parameter sets were seen in the stream. Default true.
    /// </summary>
    public bool InsertParameterSets { get; init; } = true;

    /// <summary>Resource limits; packet size and track count are checked. Defaults when null.</summary>
    public MediaLimits? Limits { get; init; }
}
