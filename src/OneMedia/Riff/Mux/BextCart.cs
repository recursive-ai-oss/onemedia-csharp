using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// The Broadcast Wave <c>bext</c> chunk (EBU Tech 3285 v2 §2 and its supplements): description, originator, origination date and time, time reference, SMPTE UMID,
/// loudness and coding history. <see cref="ToBytes"/> gives the chunk payload for <see cref="WavMuxOptions.Bext"/>.
/// </summary>
/// <remarks>
/// Text fields are ASCII, as the specification says, and must fit their fixed width (the muxer never truncates). The version written is 0 when
/// neither a UMID nor a loudness value is set, 1 when a UMID is, and 2 when a loudness value is; the loudness fields that are not set hold 0x7FFF
/// ("not set", Tech 3285 v2).
/// </remarks>
public sealed record BextChunk
{
    internal const int FixedSize = 602;

    /// <summary>Free text, up to 256 characters.</summary>
    public string? Description { get; init; }

    /// <summary>The name of the originator, up to 32 characters.</summary>
    public string? Originator { get; init; }

    /// <summary>A reference allocated by the originator, up to 32 characters.</summary>
    public string? OriginatorReference { get; init; }

    /// <summary>The date of creation, written as <c>yyyy-MM-dd</c>. Null writes ten zero bytes.</summary>
    public DateOnly? OriginationDate { get; init; }

    /// <summary>The time of creation, written as <c>HH:mm:ss</c>. Null writes eight zero bytes.</summary>
    public TimeOnly? OriginationTime { get; init; }

    /// <summary>The first sample's count since midnight, in samples at the file's rate (the 64-bit <c>TimeReference</c>).</summary>
    public ulong TimeReference { get; init; }

    /// <summary>The 64-byte SMPTE UMID (basic or extended), or null for none. A shorter value is zero-padded.</summary>
    public ReadOnlyMemory<byte>? Umid { get; init; }

    /// <summary>Integrated loudness in LUFS (stored in hundredths), or null for not set.</summary>
    public double? LoudnessValue { get; init; }

    /// <summary>Loudness range in LU, or null.</summary>
    public double? LoudnessRange { get; init; }

    /// <summary>Maximum true peak level in dBTP, or null.</summary>
    public double? MaxTruePeakLevel { get; init; }

    /// <summary>Maximum momentary loudness in LUFS, or null.</summary>
    public double? MaxMomentaryLoudness { get; init; }

    /// <summary>Maximum short-term loudness in LUFS, or null.</summary>
    public double? MaxShortTermLoudness { get; init; }

    /// <summary>The coding history: one line per processing stage, each ending in CR LF (Tech 3285 v2 §2.3). Written after the 602 fixed bytes; null or empty writes none.</summary>
    public string? CodingHistory { get; init; }

    /// <summary>Builds the chunk payload.</summary>
    /// <returns>602 bytes plus the coding history.</returns>
    /// <exception cref="ArgumentException">A text is not ASCII or does not fit its field, or the UMID is longer than 64 bytes.</exception>
    public byte[] ToBytes()
    {
        string history = CodingHistory ?? string.Empty;
        byte[] b = new byte[FixedSize + history.Length];
        TextFields.Write(b.AsSpan(0, 256), Description, nameof(Description));
        TextFields.Write(b.AsSpan(256, 32), Originator, nameof(Originator));
        TextFields.Write(b.AsSpan(288, 32), OriginatorReference, nameof(OriginatorReference));
        if (OriginationDate is { } d)
        {
            TextFields.Write(b.AsSpan(320, 10), d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), nameof(OriginationDate));
        }

        if (OriginationTime is { } t)
        {
            TextFields.Write(b.AsSpan(330, 8), t.ToString("HH:mm:ss", CultureInfo.InvariantCulture), nameof(OriginationTime));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(338), (uint)TimeReference);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(342), (uint)(TimeReference >> 32));
        bool loudness = LoudnessValue is not null || LoudnessRange is not null || MaxTruePeakLevel is not null || MaxMomentaryLoudness is not null || MaxShortTermLoudness is not null;
        ushort version = loudness ? (ushort)2 : Umid is not null ? (ushort)1 : (ushort)0;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(346), version);
        if (Umid is { } umid)
        {
            if (umid.Length > 64)
            {
                throw new ArgumentException("The UMID is longer than 64 bytes.", nameof(Umid));
            }

            umid.Span.CopyTo(b.AsSpan(348));
        }

        if (loudness)
        {
            double?[] values = [LoudnessValue, LoudnessRange, MaxTruePeakLevel, MaxMomentaryLoudness, MaxShortTermLoudness];
            for (int i = 0; i < values.Length; i++)
            {
                short v = 0x7FFF;
                if (values[i] is { } x)
                {
                    double scaled = Math.Round(x * 100.0, MidpointRounding.AwayFromZero);
                    if (!(scaled >= short.MinValue && scaled < 0x7FFF))
                    {
                        throw new ArgumentException("A loudness value does not fit the 16-bit field (hundredths of a unit).", nameof(LoudnessValue));
                    }

                    v = (short)scaled;
                }

                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(412 + (i * 2)), v);
            }
        }

        // Tech 3285 v2: 180 reserved bytes (422..601) are zero.
        if (history.Length > 0)
        {
            TextFields.Write(b.AsSpan(FixedSize), history, nameof(CodingHistory));
        }

        return b;
    }
}

/// <summary>
/// The AES46-2002 <c>cart</c> chunk (broadcast cart label): the fixed 2048 bytes of title, artist, cut and client identifiers, dates, producer, level
/// reference, post timers and URL, then free text. <see cref="ToBytes"/> gives the chunk payload for <see cref="WavMuxOptions.Cart"/>.
/// </summary>
/// <remarks>The field layout was taken from files written by broadcast software, as ExifTool's RIFF tag documentation lists it (AES46 itself was not obtained). Texts are ASCII and must fit their field.</remarks>
public sealed record CartChunk
{
    internal const int FixedSize = 2048;

    /// <summary>The four-character version, <c>0101</c> by default.</summary>
    public string Version { get; init; } = "0101";

    /// <summary>Title, up to 64 characters.</summary>
    public string? Title { get; init; }

    /// <summary>Artist, up to 64 characters.</summary>
    public string? Artist { get; init; }

    /// <summary>Cut identification, up to 64 characters.</summary>
    public string? CutId { get; init; }

    /// <summary>Client identification, up to 64 characters.</summary>
    public string? ClientId { get; init; }

    /// <summary>Category, up to 64 characters.</summary>
    public string? Category { get; init; }

    /// <summary>Classification, up to 64 characters.</summary>
    public string? Classification { get; init; }

    /// <summary>Out cue text, up to 64 characters.</summary>
    public string? OutCue { get; init; }

    /// <summary>Start date text (<c>yyyy/MM/dd</c> by convention), up to 10 characters.</summary>
    public string? StartDate { get; init; }

    /// <summary>Start time text (<c>HH:mm:ss</c>), up to 8 characters.</summary>
    public string? StartTime { get; init; }

    /// <summary>End date text, up to 10 characters.</summary>
    public string? EndDate { get; init; }

    /// <summary>End time text, up to 8 characters.</summary>
    public string? EndTime { get; init; }

    /// <summary>The name of the program that wrote the label, up to 64 characters.</summary>
    public string? ProducerAppId { get; init; }

    /// <summary>That program's version, up to 64 characters.</summary>
    public string? ProducerAppVersion { get; init; }

    /// <summary>A user-defined field, up to 64 characters.</summary>
    public string? UserDef { get; init; }

    /// <summary>The level reference: the sample value that corresponds to 0 dB (a signed 32-bit number); 0 when none.</summary>
    public int LevelReference { get; init; }

    /// <summary>Up to eight post timers: a four-character usage id and a sample position each.</summary>
    public IReadOnlyList<KeyValuePair<string, uint>>? PostTimers { get; init; }

    /// <summary>A URL, up to 1024 characters.</summary>
    public string? Url { get; init; }

    /// <summary>Free text after the fixed fields (the <c>TagText</c>), written as given; null or empty writes none.</summary>
    public string? TagText { get; init; }

    /// <summary>Builds the chunk payload.</summary>
    /// <returns>2048 bytes plus the tag text.</returns>
    /// <exception cref="ArgumentException">A text is not ASCII or does not fit its field, or there are more than eight post timers.</exception>
    public byte[] ToBytes()
    {
        string text = TagText ?? string.Empty;
        byte[] b = new byte[FixedSize + text.Length];
        TextFields.Write(b.AsSpan(0, 4), Version, nameof(Version));
        TextFields.Write(b.AsSpan(4, 64), Title, nameof(Title));
        TextFields.Write(b.AsSpan(68, 64), Artist, nameof(Artist));
        TextFields.Write(b.AsSpan(132, 64), CutId, nameof(CutId));
        TextFields.Write(b.AsSpan(196, 64), ClientId, nameof(ClientId));
        TextFields.Write(b.AsSpan(260, 64), Category, nameof(Category));
        TextFields.Write(b.AsSpan(324, 64), Classification, nameof(Classification));
        TextFields.Write(b.AsSpan(388, 64), OutCue, nameof(OutCue));
        TextFields.Write(b.AsSpan(452, 10), StartDate, nameof(StartDate));
        TextFields.Write(b.AsSpan(462, 8), StartTime, nameof(StartTime));
        TextFields.Write(b.AsSpan(470, 10), EndDate, nameof(EndDate));
        TextFields.Write(b.AsSpan(480, 8), EndTime, nameof(EndTime));
        TextFields.Write(b.AsSpan(488, 64), ProducerAppId, nameof(ProducerAppId));
        TextFields.Write(b.AsSpan(552, 64), ProducerAppVersion, nameof(ProducerAppVersion));
        TextFields.Write(b.AsSpan(616, 64), UserDef, nameof(UserDef));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(680), LevelReference);
        if (PostTimers is { Count: > 0 } timers)
        {
            if (timers.Count > 8)
            {
                throw new ArgumentException("A cart chunk has room for eight post timers.", nameof(PostTimers));
            }

            for (int i = 0; i < timers.Count; i++)
            {
                TextFields.Write(b.AsSpan(684 + (i * 8), 4), timers[i].Key, nameof(PostTimers));
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(688 + (i * 8)), timers[i].Value);
            }
        }

        TextFields.Write(b.AsSpan(1024, 1024), Url, nameof(Url));
        if (text.Length > 0)
        {
            TextFields.Write(b.AsSpan(FixedSize), text, nameof(TagText));
        }

        return b;
    }
}

internal static class TextFields
{
    // Writes an ASCII string into a fixed field that is already zero. A value that does not fit is refused, never cut.
    public static void Write(Span<byte> field, string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.Length > field.Length)
        {
            throw new ArgumentException($"{name} has {value.Length} characters and its field holds {field.Length}.", name);
        }

        foreach (char c in value)
        {
            if (c > 0x7F)
            {
                throw new ArgumentException($"{name} contains a character outside ASCII; the field is ASCII by specification.", name);
            }
        }

        Encoding.ASCII.GetBytes(value, field);
    }
}
