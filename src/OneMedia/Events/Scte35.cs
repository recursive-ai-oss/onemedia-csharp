using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace RecursiveAI.OneMedia.TimedEvents;

/// <summary>The splice command carried by a splice information section, by its <c>splice_command_type</c>.</summary>
public enum Scte35CommandType
{
    /// <summary><c>splice_null</c> (0x00): a heartbeat with no command body.</summary>
    SpliceNull = 0x00,

    /// <summary><c>splice_schedule</c> (0x04): events announced with UTC times.</summary>
    SpliceSchedule = 0x04,

    /// <summary><c>splice_insert</c> (0x05): an out or in splice point, immediate or at a PTS.</summary>
    SpliceInsert = 0x05,

    /// <summary><c>time_signal</c> (0x06): a time that carries meaning through its descriptors, usually segmentation descriptors.</summary>
    TimeSignal = 0x06,

    /// <summary><c>bandwidth_reservation</c> (0x07): padding that reserves bandwidth; no body.</summary>
    BandwidthReservation = 0x07,

    /// <summary><c>private_command</c> (0xFF): an identifier and private bytes.</summary>
    PrivateCommand = 0xFF,
}

/// <summary>
/// A <c>splice_time()</c>: an optional 33-bit PTS on the 90 kHz clock, before <c>pts_adjustment</c> is added.
/// </summary>
/// <param name="IsSpecified">The <c>time_specified_flag</c>. When false the command means "now" (or "at the next opportunity") and <paramref name="PtsTime"/> is 0.</param>
/// <param name="PtsTime">The <c>pts_time</c> in 90 kHz ticks.</param>
public readonly record struct Scte35SpliceTime(bool IsSpecified, long PtsTime);

/// <summary>A <c>break_duration()</c>: how long the break lasts.</summary>
/// <param name="AutoReturn">True when the splice returns to the network feed by itself at the end of the duration.</param>
/// <param name="Duration">The duration in 90 kHz ticks (33 bits).</param>
public readonly record struct Scte35BreakDuration(bool AutoReturn, long Duration);

/// <summary>The body of a splice command. The concrete type follows <see cref="Scte35Section.CommandType"/>.</summary>
public abstract record Scte35Command;

/// <summary>The <c>splice_null</c> and <c>bandwidth_reservation</c> commands, which have no fields.</summary>
public sealed record Scte35EmptyCommand : Scte35Command;

/// <summary>A component of a <c>splice_insert</c> whose splice is signalled per component rather than for the whole program.</summary>
/// <param name="ComponentTag">The <c>component_tag</c> that ties it to a stream through its stream identifier descriptor.</param>
/// <param name="SpliceTime">Its own splice time, when the command is not immediate.</param>
public readonly record struct Scte35SpliceComponent(int ComponentTag, Scte35SpliceTime SpliceTime);

/// <summary>A <c>splice_insert()</c> command (SCTE 35 command type 0x05).</summary>
public sealed record Scte35SpliceInsert : Scte35Command
{
    /// <summary>The <c>splice_event_id</c>.</summary>
    public required uint EventId { get; init; }

    /// <summary>The <c>splice_event_cancel_indicator</c>: an earlier event with this id is cancelled; none of the fields below is meaningful.</summary>
    public bool IsCancel { get; init; }

    /// <summary>True for an out-of-network (leave the network feed) splice, false for a return to it.</summary>
    public bool OutOfNetwork { get; init; }

    /// <summary>The <c>program_splice_flag</c>: the whole program splices together; otherwise <see cref="Components"/> lists the streams.</summary>
    public bool ProgramSplice { get; init; }

    /// <summary>The <c>splice_immediate_flag</c>: splice at the earliest opportunity, with no splice time.</summary>
    public bool SpliceImmediate { get; init; }

    /// <summary>The program-wide splice time; unspecified when immediate or when components carry their own.</summary>
    public Scte35SpliceTime SpliceTime { get; init; }

    /// <summary>The per-component splice times when <see cref="ProgramSplice"/> is false.</summary>
    public IReadOnlyList<Scte35SpliceComponent> Components { get; init; } = [];

    /// <summary>How long the break lasts, when the command states it.</summary>
    public Scte35BreakDuration? BreakDuration { get; init; }

    /// <summary>The <c>unique_program_id</c>.</summary>
    public int UniqueProgramId { get; init; }

    /// <summary>The <c>avail_num</c>: this avail's number within the avails of the program.</summary>
    public int AvailNum { get; init; }

    /// <summary>The <c>avails_expected</c>: how many avails the program has (0 when unspecified).</summary>
    public int AvailsExpected { get; init; }
}

/// <summary>One event of a <c>splice_schedule()</c> command.</summary>
public sealed record Scte35ScheduledSplice
{
    /// <summary>The <c>splice_event_id</c>.</summary>
    public required uint EventId { get; init; }

    /// <summary>True when the event is cancelled; the fields below are then unset.</summary>
    public bool IsCancel { get; init; }

    /// <summary>True for an out-of-network splice.</summary>
    public bool OutOfNetwork { get; init; }

    /// <summary>True when the whole program splices at <see cref="UtcSpliceTime"/>.</summary>
    public bool ProgramSplice { get; init; }

    /// <summary>The program-wide <c>utc_splice_time</c>: seconds since 1980-01-06T00:00:00Z with the intervening leap seconds counted (the GPS time scale; subtract the leap seconds since 1980 for UTC), when <see cref="ProgramSplice"/> is true. Checked against the reading of TSDuck 3.44, which shows 1700000000 as 2033-11-18 22:13:02.</summary>
    public uint UtcSpliceTime { get; init; }

    /// <summary>Per-component <c>utc_splice_time</c> values (component tag, seconds on the same scale as <see cref="UtcSpliceTime"/>) when <see cref="ProgramSplice"/> is false.</summary>
    public IReadOnlyList<(int ComponentTag, uint UtcSpliceTime)> Components { get; init; } = [];

    /// <summary>The break duration, when stated.</summary>
    public Scte35BreakDuration? BreakDuration { get; init; }

    /// <summary>The <c>unique_program_id</c>.</summary>
    public int UniqueProgramId { get; init; }

    /// <summary>The <c>avail_num</c>.</summary>
    public int AvailNum { get; init; }

    /// <summary>The <c>avails_expected</c>.</summary>
    public int AvailsExpected { get; init; }
}

/// <summary>A <c>splice_schedule()</c> command (type 0x04).</summary>
public sealed record Scte35SpliceSchedule(IReadOnlyList<Scte35ScheduledSplice> Splices) : Scte35Command;

/// <summary>A <c>time_signal()</c> command (type 0x06).</summary>
/// <param name="SpliceTime">The time the descriptors of the section apply to.</param>
public sealed record Scte35TimeSignal(Scte35SpliceTime SpliceTime) : Scte35Command;

/// <summary>A <c>private_command()</c> (type 0xFF).</summary>
/// <param name="Identifier">The 32-bit identifier, usually four ASCII letters registered with SMPTE.</param>
/// <param name="Data">The private bytes.</param>
public sealed record Scte35PrivateCommand(uint Identifier, ReadOnlyMemory<byte> Data) : Scte35Command;

/// <summary>A <c>splice_descriptor()</c>. Descriptors the library does not decode are kept as this base type with their bytes.</summary>
public record Scte35Descriptor
{
    /// <summary>The <c>splice_descriptor_tag</c>.</summary>
    public required int Tag { get; init; }

    /// <summary>The <c>identifier</c>, 0x43554549 (<c>CUEI</c>) for the descriptors SCTE 35 defines.</summary>
    public uint Identifier { get; init; }

    /// <summary>The descriptor bytes after the identifier.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }
}

/// <summary>The avail descriptor (tag 0x00).</summary>
public sealed record Scte35AvailDescriptor : Scte35Descriptor
{
    /// <summary>The <c>provider_avail_id</c>.</summary>
    public uint ProviderAvailId { get; init; }
}

/// <summary>The DTMF descriptor (tag 0x01).</summary>
public sealed record Scte35DtmfDescriptor : Scte35Descriptor
{
    /// <summary>The <c>preroll</c>, in tenths of a second.</summary>
    public int PrerollTenths { get; init; }

    /// <summary>The DTMF characters.</summary>
    public string Characters { get; init; } = string.Empty;
}

/// <summary>The time descriptor (tag 0x03): a TAI time for the splice.</summary>
public sealed record Scte35TimeDescriptor : Scte35Descriptor
{
    /// <summary>TAI seconds since 1970-01-01.</summary>
    public long TaiSeconds { get; init; }

    /// <summary>Nanoseconds past <see cref="TaiSeconds"/>.</summary>
    public uint TaiNanoseconds { get; init; }

    /// <summary>The offset in seconds to subtract to get UTC.</summary>
    public int UtcOffset { get; init; }
}

/// <summary>The segmentation descriptor (tag 0x02), which says what a <c>time_signal</c> marks: a program, chapter, break or advertisement boundary.</summary>
public sealed record Scte35SegmentationDescriptor : Scte35Descriptor
{
    /// <summary>The <c>segmentation_event_id</c>.</summary>
    public uint EventId { get; init; }

    /// <summary>True when the event is cancelled; the fields below are then unset.</summary>
    public bool IsCancel { get; init; }

    /// <summary>The <c>program_segmentation_flag</c>.</summary>
    public bool ProgramSegmentation { get; init; }

    /// <summary>The <c>delivery_not_restricted_flag</c>: when true the four restriction fields below are not sent and have no meaning.</summary>
    public bool DeliveryNotRestricted { get; init; }

    /// <summary>The <c>web_delivery_allowed_flag</c> (meaningful only when delivery is restricted).</summary>
    public bool WebDeliveryAllowed { get; init; }

    /// <summary>The <c>no_regional_blackout_flag</c> (meaningful only when delivery is restricted).</summary>
    public bool NoRegionalBlackout { get; init; }

    /// <summary>The <c>archive_allowed_flag</c> (meaningful only when delivery is restricted).</summary>
    public bool ArchiveAllowed { get; init; }

    /// <summary>The <c>device_restrictions</c> code (0 to 3), meaningful only when delivery is restricted.</summary>
    public int DeviceRestrictions { get; init; }

    /// <summary>Per-component PTS offsets (component tag, 90 kHz ticks) when <see cref="ProgramSegmentation"/> is false.</summary>
    public IReadOnlyList<(int ComponentTag, long PtsOffset)> Components { get; init; } = [];

    /// <summary>The <c>segmentation_duration</c> in 90 kHz ticks (40 bits), when stated.</summary>
    public long? Duration { get; init; }

    /// <summary>The <c>segmentation_upid_type</c>.</summary>
    public int UpidType { get; init; }

    /// <summary>The UPID bytes.</summary>
    public ReadOnlyMemory<byte> Upid { get; init; }

    /// <summary>The <c>segmentation_type_id</c>, for example 0x10 provider advertisement start, 0x11 end, 0x34 provider placement opportunity start.</summary>
    public int TypeId { get; init; }

    /// <summary>The <c>segment_num</c>.</summary>
    public int SegmentNum { get; init; }

    /// <summary>The <c>segments_expected</c>.</summary>
    public int SegmentsExpected { get; init; }

    /// <summary>The <c>sub_segment_num</c>, present only for some type ids; -1 when absent.</summary>
    public int SubSegmentNum { get; init; } = -1;

    /// <summary>The <c>sub_segments_expected</c>, -1 when absent.</summary>
    public int SubSegmentsExpected { get; init; } = -1;
}

/// <summary>
/// A <c>splice_info_section()</c>, the message SCTE 35 carries in a transport stream PID of stream type 0x86 or in an <c>emsg</c> box.
/// </summary>
/// <remarks>
/// Parsing is tolerant: a section whose CRC fails, or whose command or a descriptor is cut short, is still returned with what could be
/// read, <see cref="CrcValid"/> false and a defect in the log. An encrypted section (<see cref="IsEncrypted"/>) has no decoded
/// command or descriptors, since the library holds no keys; <see cref="Data"/> keeps the bytes.
/// </remarks>
public sealed class Scte35Section
{
    /// <summary>Shortest valid section: header to command type, empty descriptor loop and the CRC.</summary>
    public const int MinimumLength = 14 + 2 + 4;

    internal Scte35Section()
    {
    }

    /// <summary>The <c>table_id</c>, 0xFC for a splice information section.</summary>
    public int TableId { get; private set; }

    /// <summary>The <c>sap_type</c> (2 bits; 3 means unspecified).</summary>
    public int SapType { get; private set; }

    /// <summary>The <c>protocol_version</c>, 0 so far.</summary>
    public int ProtocolVersion { get; private set; }

    /// <summary>True when the command and descriptors are encrypted.</summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>The <c>encryption_algorithm</c> (0 none, 1 DES-ECB, 2 DES-CBC, 3 triple DES, others user private).</summary>
    public int EncryptionAlgorithm { get; private set; }

    /// <summary>The <c>pts_adjustment</c> in 90 kHz ticks (33 bits), added modulo 2^33 to every PTS in the message.</summary>
    public long PtsAdjustment { get; private set; }

    /// <summary>The <c>cw_index</c>, the control word used for encryption.</summary>
    public int CwIndex { get; private set; }

    /// <summary>The 12-bit <c>tier</c> (0xFFF means all tiers).</summary>
    public int Tier { get; private set; }

    /// <summary>The raw <c>splice_command_type</c>.</summary>
    public int CommandTypeCode { get; private set; }

    /// <summary>The command type when the code is one SCTE 35 defines.</summary>
    public Scte35CommandType? CommandType => CommandTypeCode is 0x00 or 0x04 or 0x05 or 0x06 or 0x07 or 0xFF ? (Scte35CommandType)CommandTypeCode : null;

    /// <summary>The decoded command, or null when the section is encrypted, the command type is unknown or the command is cut short.</summary>
    public Scte35Command? Command { get; private set; }

    /// <summary>The splice descriptors, empty when encrypted.</summary>
    public IReadOnlyList<Scte35Descriptor> Descriptors { get; private set; } = [];

    /// <summary>The <c>CRC_32</c> as stored.</summary>
    public uint Crc32 { get; private set; }

    /// <summary>True when the stored CRC matches the section bytes (ISO/IEC 13818-1 Annex A CRC-32).</summary>
    public bool CrcValid { get; private set; }

    /// <summary>The whole section as parsed, header to CRC.</summary>
    public ReadOnlyMemory<byte> Data { get; private set; }

    /// <summary>
    /// The time this message is about on the 90 kHz clock before adjustment: the splice time of a <c>splice_insert</c> or <c>time_signal</c>,
    /// or null when the command carries none (immediate, <c>splice_null</c>, schedule, private).
    /// </summary>
    public long? SplicePts
    {
        get
        {
            Scte35SpliceTime t = Command switch
            {
                Scte35SpliceInsert { SpliceImmediate: false, ProgramSplice: true } i => i.SpliceTime,
                Scte35SpliceInsert { SpliceImmediate: false } i when i.Components.Count > 0 => i.Components[0].SpliceTime,
                Scte35TimeSignal s => s.SpliceTime,
                _ => default,
            };
            return t.IsSpecified ? t.PtsTime : null;
        }
    }

    /// <summary>
    /// <see cref="SplicePts"/> plus <see cref="PtsAdjustment"/>, modulo 2^33: the splice time on the stream's own PTS clock (SCTE 35 §9.2),
    /// in 90 kHz ticks. Null when there is no splice time.
    /// </summary>
    public long? AdjustedSplicePts => SplicePts is long p ? (p + PtsAdjustment) & 0x1_FFFF_FFFFL : null;

    /// <summary>Parses a section.</summary>
    /// <param name="section">The section bytes from <c>table_id</c> through <c>CRC_32</c>.</param>
    /// <param name="result">The parsed section when the result is true.</param>
    /// <param name="defects">Receives deviations found (CRC, lengths, reserved values), or null to ignore them.</param>
    /// <param name="offset">Where the section lies in its source, for the defects' offsets.</param>
    /// <returns>False when the bytes are too short to hold the fixed fields or the table id is not 0xFC.</returns>
    public static bool TryParse(ReadOnlySpan<byte> section, out Scte35Section? result, DefectLog? defects = null, long offset = -1)
    {
        result = null;
        if (section.Length < MinimumLength || section[0] != 0xFC)
        {
            return false;
        }

        // SCTE 35 §9.6: section_syntax_indicator is 0 and private_indicator 0 (this is a short section: no section number or version).
        int sectionLength = BinaryPrimitives.ReadUInt16BigEndian(section[1..]) & 0x0FFF;
        int total = 3 + sectionLength;
        if (total > section.Length)
        {
            defects?.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, $"A splice section declares {sectionLength} bytes after its length field but only {section.Length - 3} are present.", "scte35.truncated");
            total = section.Length;
        }
        else if (total < section.Length)
        {
            section = section[..total];
        }

        if ((section[1] & 0x80) != 0)
        {
            defects?.Add(DefectKind.InvalidValue, DefectSeverity.Info, offset, "A splice section sets section_syntax_indicator, which must be 0.", "scte35.syntax-indicator");
        }

        if (total < MinimumLength)
        {
            return false;
        }

        Scte35Section s = new()
        {
            TableId = section[0],
            SapType = (section[1] >> 4) & 3,
            ProtocolVersion = section[3],
            IsEncrypted = (section[4] & 0x80) != 0,
            EncryptionAlgorithm = (section[4] >> 1) & 0x3F,
            PtsAdjustment = ((long)(section[4] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(section[5..]),
            CwIndex = section[9],
            Tier = (section[10] << 4) | (section[11] >> 4),
            CommandTypeCode = section[13],
            Data = section.ToArray(), // a section is at most 4096 bytes; the copy keeps the result independent of the caller's buffer
        };
        int commandLength = ((section[11] & 0x0F) << 8) | section[12];
        s.Crc32 = BinaryPrimitives.ReadUInt32BigEndian(section[(total - 4)..]);
        s.CrcValid = MpegTs.Mpeg2Crc.Compute(section) == 0;
        if (!s.CrcValid)
        {
            defects?.Add(DefectKind.ChecksumMismatch, DefectSeverity.Warning, offset, "A splice section fails its CRC-32.", "scte35.crc");
        }

        if (s.ProtocolVersion != 0)
        {
            defects?.Add(DefectKind.ReservedValue, DefectSeverity.Info, offset, $"A splice section has protocol_version {s.ProtocolVersion}; only 0 is defined.", "scte35.protocol-version");
        }

        // The command and descriptors end before the (E_CRC_32 when encrypted and) CRC_32.
        int end = total - 4 - (s.IsEncrypted ? 4 : 0);
        if (s.IsEncrypted)
        {
            result = s;
            return true;
        }

        ReadOnlySpan<byte> body = section[14..Math.Max(14, end)];
        // 0xFFF is the legacy "length not stated" value: the command's own syntax then says where it ends.
        int used = 0;
        bool known = commandLength != 0xFFF && commandLength <= body.Length;
        ReadOnlySpan<byte> commandBytes = known ? body[..commandLength] : body;
        s.Command = ParseCommand(s.CommandTypeCode, commandBytes, out used, defects, offset);
        if (s.Command is null && s.CommandType is not null)
        {
            defects?.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, "A splice command is cut short.", "scte35.command-truncated");
        }

        int descriptorStart = known ? commandLength : used;
        if (s.Command is null && !known)
        {
            descriptorStart = body.Length;
        }

        if (descriptorStart + 2 <= body.Length)
        {
            int loop = BinaryPrimitives.ReadUInt16BigEndian(body[descriptorStart..]);
            int available = body.Length - descriptorStart - 2;
            if (loop > available)
            {
                defects?.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, offset, $"The descriptor loop declares {loop} bytes but {available} remain.", "scte35.descriptor-loop");
                loop = available;
            }

            s.Descriptors = ParseDescriptors(body.Slice(descriptorStart + 2, loop), defects, offset);
        }

        result = s;
        return true;
    }

    /// <summary>Reads only the splice time of a section, adjusted by <c>pts_adjustment</c>, without building the object model (the demuxer's per-section path).</summary>
    internal static bool TryPeekAdjustedPts(ReadOnlySpan<byte> section, out long adjusted)
    {
        adjusted = 0;
        if (section.Length < MinimumLength || section[0] != 0xFC || (section[4] & 0x80) != 0)
        {
            return false;
        }

        ReadOnlySpan<byte> command = section[14..];
        int timeAt;
        switch (section[13])
        {
            case 0x06:
                timeAt = 0;
                break;
            case 0x05:
                // splice_event_id(4), cancel flag byte, flags byte: program_splice (0x40) and immediate (0x10) decide whether a time follows.
                if (command.Length < 6 || (command[4] & 0x80) != 0 || (command[5] & 0x40) == 0 || (command[5] & 0x10) != 0)
                {
                    return false;
                }

                timeAt = 6;
                break;
            default:
                return false;
        }

        if (command.Length < timeAt + 5 || (command[timeAt] & 0x80) == 0)
        {
            return false;
        }

        long pts = ((long)(command[timeAt] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(command[(timeAt + 1)..]);
        long adjustment = ((long)(section[4] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(section[5..]);
        adjusted = (pts + adjustment) & 0x1_FFFF_FFFFL;
        return true;
    }

    private static Scte35Command? ParseCommand(int type, ReadOnlySpan<byte> b, out int used, DefectLog? defects, long offset)
    {
        used = 0;
        switch (type)
        {
            case 0x00:
            case 0x07:
                return new Scte35EmptyCommand();
            case 0x06:
            {
                if (!TryReadSpliceTime(b, out Scte35SpliceTime t, out used))
                {
                    return null;
                }

                return new Scte35TimeSignal(t);
            }

            case 0x05:
                return ParseSpliceInsert(b, out used);
            case 0x04:
                return ParseSpliceSchedule(b, out used);
            case 0xFF:
                if (b.Length < 4)
                {
                    return null;
                }

                used = b.Length;
                return new Scte35PrivateCommand(BinaryPrimitives.ReadUInt32BigEndian(b), b[4..].ToArray()); // a private command is at most 4 KiB
            default:
                defects?.Add(DefectKind.ReservedValue, DefectSeverity.Info, offset, $"Unknown splice command type 0x{type:x2}; its bytes are kept in the section only.", "scte35.command-type");
                used = b.Length;
                return null;
        }
    }

    // SCTE 35 §9.7.4: splice_time(): time_specified_flag, then 6 reserved bits and a 33-bit pts_time, or 7 reserved bits.
    private static bool TryReadSpliceTime(ReadOnlySpan<byte> b, out Scte35SpliceTime time, out int length)
    {
        time = default;
        length = 0;
        if (b.IsEmpty)
        {
            return false;
        }

        if ((b[0] & 0x80) == 0)
        {
            length = 1;
            return true;
        }

        if (b.Length < 5)
        {
            return false;
        }

        long pts = ((long)(b[0] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(b[1..]);
        time = new Scte35SpliceTime(true, pts);
        length = 5;
        return true;
    }

    // §9.7.5: break_duration(): auto_return, 6 reserved bits, 33-bit duration.
    private static bool TryReadBreakDuration(ReadOnlySpan<byte> b, out Scte35BreakDuration duration)
    {
        duration = default;
        if (b.Length < 5)
        {
            return false;
        }

        duration = new Scte35BreakDuration((b[0] & 0x80) != 0, ((long)(b[0] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(b[1..]));
        return true;
    }

    private static Scte35SpliceInsert? ParseSpliceInsert(ReadOnlySpan<byte> b, out int used)
    {
        used = 0;
        if (b.Length < 5)
        {
            return null;
        }

        uint id = BinaryPrimitives.ReadUInt32BigEndian(b);
        bool cancel = (b[4] & 0x80) != 0;
        int p = 5;
        if (cancel)
        {
            used = p;
            return new Scte35SpliceInsert { EventId = id, IsCancel = true };
        }

        if (b.Length < p + 1)
        {
            return null;
        }

        byte flags = b[p++];
        bool outOfNetwork = (flags & 0x80) != 0, program = (flags & 0x40) != 0, hasDuration = (flags & 0x20) != 0, immediate = (flags & 0x10) != 0;
        Scte35SpliceTime time = default;
        List<Scte35SpliceComponent> components = [];
        if (program && !immediate)
        {
            if (!TryReadSpliceTime(b[p..], out time, out int n))
            {
                return null;
            }

            p += n;
        }

        if (!program)
        {
            if (b.Length < p + 1)
            {
                return null;
            }

            int count = b[p++];
            for (int i = 0; i < count; i++)
            {
                if (b.Length < p + 1)
                {
                    return null;
                }

                int tag = b[p++];
                Scte35SpliceTime ct = default;
                if (!immediate)
                {
                    if (!TryReadSpliceTime(b[p..], out ct, out int n))
                    {
                        return null;
                    }

                    p += n;
                }

                components.Add(new Scte35SpliceComponent(tag, ct));
            }
        }

        Scte35BreakDuration? duration = null;
        if (hasDuration)
        {
            if (!TryReadBreakDuration(b[p..], out Scte35BreakDuration d))
            {
                return null;
            }

            duration = d;
            p += 5;
        }

        if (b.Length < p + 4)
        {
            return null;
        }

        used = p + 4;
        return new Scte35SpliceInsert
        {
            EventId = id,
            OutOfNetwork = outOfNetwork,
            ProgramSplice = program,
            SpliceImmediate = immediate,
            SpliceTime = time,
            Components = components,
            BreakDuration = duration,
            UniqueProgramId = BinaryPrimitives.ReadUInt16BigEndian(b[p..]),
            AvailNum = b[p + 2],
            AvailsExpected = b[p + 3],
        };
    }

    private static Scte35SpliceSchedule? ParseSpliceSchedule(ReadOnlySpan<byte> b, out int used)
    {
        used = 0;
        if (b.IsEmpty)
        {
            return null;
        }

        int count = b[0];
        int p = 1;
        List<Scte35ScheduledSplice> list = [];
        for (int i = 0; i < count; i++)
        {
            if (b.Length < p + 5)
            {
                return null;
            }

            uint id = BinaryPrimitives.ReadUInt32BigEndian(b[p..]);
            bool cancel = (b[p + 4] & 0x80) != 0;
            p += 5;
            if (cancel)
            {
                list.Add(new Scte35ScheduledSplice { EventId = id, IsCancel = true });
                continue;
            }

            if (b.Length < p + 1)
            {
                return null;
            }

            byte flags = b[p++];
            bool outOfNetwork = (flags & 0x80) != 0, program = (flags & 0x40) != 0, hasDuration = (flags & 0x20) != 0;
            uint utc = 0;
            List<(int, uint)> components = [];
            if (program)
            {
                if (b.Length < p + 4)
                {
                    return null;
                }

                utc = BinaryPrimitives.ReadUInt32BigEndian(b[p..]);
                p += 4;
            }
            else
            {
                if (b.Length < p + 1)
                {
                    return null;
                }

                int n = b[p++];
                for (int c = 0; c < n; c++)
                {
                    if (b.Length < p + 5)
                    {
                        return null;
                    }

                    components.Add((b[p], BinaryPrimitives.ReadUInt32BigEndian(b[(p + 1)..])));
                    p += 5;
                }
            }

            Scte35BreakDuration? duration = null;
            if (hasDuration)
            {
                if (!TryReadBreakDuration(b[p..], out Scte35BreakDuration d))
                {
                    return null;
                }

                duration = d;
                p += 5;
            }

            if (b.Length < p + 4)
            {
                return null;
            }

            list.Add(new Scte35ScheduledSplice
            {
                EventId = id,
                OutOfNetwork = outOfNetwork,
                ProgramSplice = program,
                UtcSpliceTime = utc,
                Components = components,
                BreakDuration = duration,
                UniqueProgramId = BinaryPrimitives.ReadUInt16BigEndian(b[p..]),
                AvailNum = b[p + 2],
                AvailsExpected = b[p + 3],
            });
            p += 4;
        }

        used = p;
        return new Scte35SpliceSchedule(new ReadOnlyCollection<Scte35ScheduledSplice>(list));
    }

    private static List<Scte35Descriptor> ParseDescriptors(ReadOnlySpan<byte> loop, DefectLog? defects, long offset)
    {
        List<Scte35Descriptor> list = [];
        int p = 0;
        while (p + 2 <= loop.Length)
        {
            int tag = loop[p];
            int length = loop[p + 1];
            if (p + 2 + length > loop.Length)
            {
                defects?.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, offset, $"A splice descriptor (tag 0x{tag:x2}) is longer than the loop that holds it.", "scte35.descriptor-length");
                break;
            }

            ReadOnlySpan<byte> d = loop.Slice(p + 2, length);
            p += 2 + length;
            if (d.Length < 4)
            {
                list.Add(new Scte35Descriptor { Tag = tag, Body = d.ToArray() });
                continue;
            }

            uint identifier = BinaryPrimitives.ReadUInt32BigEndian(d);
            ReadOnlySpan<byte> body = d[4..];
            list.Add(identifier == 0x43554549 ? Typed(tag, identifier, body) : new Scte35Descriptor { Tag = tag, Identifier = identifier, Body = body.ToArray() });
        }

        return list;
    }

    private static Scte35Descriptor Typed(int tag, uint identifier, ReadOnlySpan<byte> body)
    {
        ReadOnlyMemory<byte> raw = body.ToArray(); // descriptors are at most 255 bytes
        switch (tag)
        {
            case 0x00 when body.Length >= 4:
                return new Scte35AvailDescriptor { Tag = tag, Identifier = identifier, Body = raw, ProviderAvailId = BinaryPrimitives.ReadUInt32BigEndian(body) };
            case 0x01 when body.Length >= 2:
            {
                int count = body[1] >> 5;
                int n = Math.Min(count, body.Length - 2);
                return new Scte35DtmfDescriptor { Tag = tag, Identifier = identifier, Body = raw, PrerollTenths = body[0], Characters = System.Text.Encoding.Latin1.GetString(body.Slice(2, n)) };
            }

            case 0x02:
                return ParseSegmentation(tag, identifier, body, raw);
            case 0x03 when body.Length >= 12:
                return new Scte35TimeDescriptor
                {
                    Tag = tag,
                    Identifier = identifier,
                    Body = raw,
                    TaiSeconds = ((long)BinaryPrimitives.ReadUInt16BigEndian(body) << 32) | BinaryPrimitives.ReadUInt32BigEndian(body[2..]),
                    TaiNanoseconds = BinaryPrimitives.ReadUInt32BigEndian(body[6..]),
                    UtcOffset = BinaryPrimitives.ReadUInt16BigEndian(body[10..]),
                };
            default:
                return new Scte35Descriptor { Tag = tag, Identifier = identifier, Body = raw };
        }
    }

    private static Scte35Descriptor ParseSegmentation(int tag, uint identifier, ReadOnlySpan<byte> b, ReadOnlyMemory<byte> raw)
    {
        Scte35Descriptor Plain() => new() { Tag = tag, Identifier = identifier, Body = raw };
        if (b.Length < 5)
        {
            return Plain();
        }

        uint id = BinaryPrimitives.ReadUInt32BigEndian(b);
        if ((b[4] & 0x80) != 0)
        {
            return new Scte35SegmentationDescriptor { Tag = tag, Identifier = identifier, Body = raw, EventId = id, IsCancel = true };
        }

        if (b.Length < 6)
        {
            return Plain();
        }

        byte flags = b[5];
        bool program = (flags & 0x80) != 0, hasDuration = (flags & 0x40) != 0, notRestricted = (flags & 0x20) != 0;
        int p = 6;
        List<(int, long)> components = [];
        if (!program)
        {
            if (b.Length < p + 1)
            {
                return Plain();
            }

            int n = b[p++];
            for (int i = 0; i < n; i++)
            {
                if (b.Length < p + 6)
                {
                    return Plain();
                }

                components.Add((b[p], ((long)(b[p + 1] & 1) << 32) | BinaryPrimitives.ReadUInt32BigEndian(b[(p + 2)..])));
                p += 6;
            }
        }

        long? duration = null;
        if (hasDuration)
        {
            if (b.Length < p + 5)
            {
                return Plain();
            }

            duration = ((long)b[p] << 32) | BinaryPrimitives.ReadUInt32BigEndian(b[(p + 1)..]);
            p += 5;
        }

        if (b.Length < p + 2)
        {
            return Plain();
        }

        int upidType = b[p], upidLength = b[p + 1];
        p += 2;
        if (b.Length < p + upidLength + 3)
        {
            return Plain();
        }

        ReadOnlyMemory<byte> upid = b.Slice(p, upidLength).ToArray(); // a UPID is at most 255 bytes
        p += upidLength;
        int typeId = b[p], segmentNum = b[p + 1], expected = b[p + 2];
        p += 3;
        int sub = -1, subExpected = -1;
        // Segmentation types 0x34, 0x36, 0x38 and 0x3A (placement opportunities) add sub-segment counts after segments_expected.
        if (typeId is 0x34 or 0x36 or 0x38 or 0x3A && b.Length >= p + 2)
        {
            sub = b[p];
            subExpected = b[p + 1];
        }

        return new Scte35SegmentationDescriptor
        {
            Tag = tag,
            Identifier = identifier,
            Body = raw,
            EventId = id,
            ProgramSegmentation = program,
            DeliveryNotRestricted = notRestricted,
            WebDeliveryAllowed = !notRestricted && (flags & 0x10) != 0,
            NoRegionalBlackout = !notRestricted && (flags & 0x08) != 0,
            ArchiveAllowed = !notRestricted && (flags & 0x04) != 0,
            DeviceRestrictions = notRestricted ? 0 : flags & 3,
            Components = components,
            Duration = duration,
            UpidType = upidType,
            Upid = upid,
            TypeId = typeId,
            SegmentNum = segmentNum,
            SegmentsExpected = expected,
            SubSegmentNum = sub,
            SubSegmentsExpected = subExpected,
        };
    }
}
