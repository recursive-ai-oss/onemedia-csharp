using System.Buffers.Binary;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia;

/// <summary>How a piece of an output file is produced.</summary>
public enum OutputPieceKind
{
    /// <summary>The bytes are copied unchanged from a range of the source file.</summary>
    Copy = 0,

    /// <summary>The bytes are new content held in memory (a rewritten header, a replaced metadata box).</summary>
    Data,

    /// <summary>The bytes are produced while writing by an <see cref="IPieceGenerator"/> (a large table with every offset moved).</summary>
    Generated,
}

/// <summary>
/// Produces the bytes of one <see cref="OutputPieceKind.Generated"/> piece while the file is written, so that content that would be
/// too large or too slow to hold (an offset table with millions of entries) is streamed in constant memory.
/// </summary>
public interface IPieceGenerator
{
    /// <summary>
    /// Writes exactly the number of bytes the piece declared. Must be deterministic: it may be run more than once (once to compute a
    /// checksum, once to write).
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="source">The original file, for generators that transform what it holds.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    void Write(Stream output, IMediaSource source, CancellationToken cancellationToken);
}

/// <summary>One run of the output file: copied from the source by range, held in memory, or generated while writing.</summary>
public sealed class OutputPiece
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly IPieceGenerator? _generator;

    private OutputPiece(OutputPieceKind kind, long length, ByteRange range, ReadOnlyMemory<byte> bytes, IPieceGenerator? generator)
    {
        Kind = kind;
        Length = length;
        SourceRange = range;
        _bytes = bytes;
        _generator = generator;
    }

    /// <summary>How the bytes are produced.</summary>
    public OutputPieceKind Kind { get; }

    /// <summary>Number of bytes the piece contributes to the output.</summary>
    public long Length { get; }

    /// <summary>For <see cref="OutputPieceKind.Copy"/>, the source range copied; <see cref="ByteRange.None"/> otherwise.</summary>
    public ByteRange SourceRange { get; }

    /// <summary>For <see cref="OutputPieceKind.Data"/>, the bytes; empty otherwise.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>For <see cref="OutputPieceKind.Generated"/>, the generator; null otherwise.</summary>
    public IPieceGenerator? Generator => _generator;

    /// <summary>A piece copied unchanged from the source.</summary>
    /// <param name="offset">Absolute source offset.</param>
    /// <param name="length">Number of bytes; must not be negative.</param>
    /// <returns>The piece.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is negative.</exception>
    public static OutputPiece Copy(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new OutputPiece(OutputPieceKind.Copy, length, new ByteRange(offset, length), default, null);
    }

    /// <summary>A piece of new content. The memory must not change while the plan is in use.</summary>
    /// <param name="bytes">The content.</param>
    /// <returns>The piece.</returns>
    public static OutputPiece Data(ReadOnlyMemory<byte> bytes) => new(OutputPieceKind.Data, bytes.Length, ByteRange.None, bytes, null);

    /// <summary>A piece produced while writing.</summary>
    /// <param name="length">Exact number of bytes the generator writes.</param>
    /// <param name="generator">The generator.</param>
    /// <returns>The piece.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static OutputPiece Generated(long length, IPieceGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new OutputPiece(OutputPieceKind.Generated, length, ByteRange.None, default, generator);
    }
}

/// <summary>Writes a run of zero bytes while the file is written, so padding of any size costs a small buffer and no allocation.</summary>
internal sealed class ZeroFill(long length) : IPieceGenerator
{
    private static readonly byte[] Zeros = new byte[64 * 1024];

    public void Write(Stream output, IMediaSource source, CancellationToken cancellationToken)
    {
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(remaining, Zeros.Length);
            output.Write(Zeros, 0, n);
            remaining -= n;
        }
    }
}

/// <summary>One change a plan makes, for the write-back report.</summary>
/// <param name="Description">What changed, in words (for example <c>replace 'udta'</c>).</param>
/// <param name="Source">The range of the original file that is replaced, removed or moved; <see cref="ByteRange.None"/> for an insertion.</param>
/// <param name="Output">The range of the new file that holds the new content; its length is 0 for a removal.</param>
public readonly record struct WriteChange(string Description, ByteRange Source, ByteRange Output);

/// <summary>
/// A description of a new file as a sequence of pieces: ranges copied from the original and new content. Untouched media payload is
/// always a <see cref="OutputPieceKind.Copy"/> piece, so it is never read into memory or re-serialised. Plans are immutable and hold no
/// file content beyond the small new content of the edit.
/// </summary>
public sealed class EditPlan
{
    internal EditPlan(IReadOnlyList<OutputPiece> pieces, long sourceLength, IReadOnlyList<WriteChange> changes, long paddingUsed, long paddingAvailable, long paddingRequired, string? shortfallIn = null)
    {
        ShortfallIn = shortfallIn;
        Pieces = pieces;
        SourceLength = sourceLength;
        Changes = changes;
        PaddingUsed = paddingUsed;
        PaddingAvailable = paddingAvailable;
        PaddingRequired = paddingRequired;
        long total = 0;
        bool same = true;
        foreach (OutputPiece p in pieces)
        {
            if (p.Kind == OutputPieceKind.Copy && p.SourceRange.Offset != total)
            {
                same = false;
            }

            total += p.Length;
        }

        Length = total;
        IsSameLayout = same && total == sourceLength;
    }

    /// <summary>The pieces in output order.</summary>
    public IReadOnlyList<OutputPiece> Pieces { get; }

    /// <summary>Length of the original file the plan was made for; writing refuses a file whose length differs.</summary>
    public long SourceLength { get; }

    /// <summary>Length of the new file.</summary>
    public long Length { get; }

    /// <summary>The changes the plan makes, in output order.</summary>
    public IReadOnlyList<WriteChange> Changes { get; }

    /// <summary>
    /// True when every copied range keeps its position and the length is unchanged, so the new file differs from the original only in the
    /// non-copied pieces and the plan can be applied in place. This is the test <see cref="SafeWriter.SaveInPlace"/> uses.
    /// </summary>
    public bool IsSameLayout { get; }

    /// <summary>Bytes of existing padding (<c>free</c>/<c>skip</c> boxes, Void elements, <c>JUNK</c> chunks) the plan uses up.</summary>
    public long PaddingUsed { get; }

    /// <summary>Bytes of padding that were found beside the edited nodes and could be used (the used part included).</summary>
    public long PaddingAvailable { get; }

    /// <summary>
    /// When the plan is not <see cref="IsSameLayout"/>: how many more bytes of padding the edit needed to fit in place (the sum of the
    /// shortfalls, at least 1 per edit that did not fit). 0 when the plan fits.
    /// </summary>
    public long PaddingRequired { get; }

    /// <summary>
    /// Which part of the file lacks the room when the edit needs more than the padding gives (for example <c>APEv2 tag</c>: that tag grows and has no
    /// padding, whatever padding another tag in the file has), or null when the plan does not say or fits.
    /// </summary>
    public string? ShortfallIn { get; }
}

/// <summary>What a write did.</summary>
public enum WriteMode
{
    /// <summary>A new file was written at a different path.</summary>
    SaveAs = 0,

    /// <summary>The original was replaced atomically by a new file.</summary>
    Save,

    /// <summary>Only the changed byte ranges of the original were overwritten.</summary>
    InPlace,
}

/// <summary>The write-back report: what changed, where, and how much padding it used.</summary>
public sealed class WriteReport
{
    internal WriteReport(WriteMode mode, EditPlan plan, long bytesCopied, long bytesWritten)
    {
        Mode = mode;
        SourceLength = plan.SourceLength;
        OutputLength = plan.Length;
        BytesCopied = bytesCopied;
        BytesWritten = bytesWritten;
        Changes = plan.Changes;
        PaddingUsed = plan.PaddingUsed;
        PaddingAvailable = plan.PaddingAvailable;
        LayoutPreserved = plan.IsSameLayout;
    }

    /// <summary>How the file was written.</summary>
    public WriteMode Mode { get; }

    /// <summary>Length of the original file.</summary>
    public long SourceLength { get; }

    /// <summary>Length of the file after the write.</summary>
    public long OutputLength { get; }

    /// <summary>Bytes taken unchanged from the original and written to a new file; 0 for an in-place write, which touches none.</summary>
    public long BytesCopied { get; }

    /// <summary>Bytes of new content written (rewritten boxes, moved offsets, padding).</summary>
    public long BytesWritten { get; }

    /// <summary>Each change with its source and output byte range. In-place writes list the exact ranges overwritten.</summary>
    public IReadOnlyList<WriteChange> Changes { get; }

    /// <summary>Bytes of existing padding the edit used.</summary>
    public long PaddingUsed { get; }

    /// <summary>Bytes of padding that were available beside the edited nodes.</summary>
    public long PaddingAvailable { get; }

    /// <summary>True when everything outside the changed ranges kept its position (no media moved).</summary>
    public bool LayoutPreserved { get; }
}

/// <summary>
/// An in-place save was refused because the edit does not fit in the padding the file already has. Nothing was written. Save to a new
/// file (<see cref="SafeWriter.SaveAs"/>, <see cref="SafeWriter.Save"/>) to make room.
/// </summary>
public sealed class PaddingRequiredException : MediaException
{
    /// <summary>Creates the exception.</summary>
    public PaddingRequiredException() : this(0, 0) { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public PaddingRequiredException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public PaddingRequiredException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>Creates the exception for a plan.</summary>
    /// <param name="required">Bytes of padding still missing.</param>
    /// <param name="available">Bytes of padding that were available.</param>
    public PaddingRequiredException(long required, long available)
        : base($"The edit does not fit in the existing padding: {required} more byte(s) of padding are needed ({available} available). Save to a new file instead.")
    {
        Required = required;
        Available = available;
    }

    /// <summary>Creates the exception for a plan whose shortfall is in a named part of the file.</summary>
    /// <param name="required">Bytes of padding still missing.</param>
    /// <param name="available">Bytes of padding that were available beside the edited nodes.</param>
    /// <param name="shortfallIn">The part of the file that lacks the room, for example <c>APEv2 tag</c>.</param>
    public PaddingRequiredException(long required, long available, string? shortfallIn)
        : base(shortfallIn is null
            ? $"The edit does not fit in the existing padding: {required} more byte(s) of padding are needed ({available} available). Save to a new file instead."
            : $"The edit does not fit in place: the {shortfallIn} would grow by {required} byte(s) and has no padding to take it (padding elsewhere in the file, {available} byte(s), cannot help). Save to a new file instead, or limit the edit to the tag systems that have room.")
    {
        Required = required;
        Available = available;
        ShortfallIn = shortfallIn;
    }

    /// <summary>The part of the file that lacks the room, or null when the edit simply needs more padding.</summary>
    public string? ShortfallIn { get; }

    /// <summary>Bytes of additional padding the edit needed.</summary>
    public long Required { get; }

    /// <summary>Bytes of padding that were available beside the edited nodes.</summary>
    public long Available { get; }
}

/// <summary>Accumulates pieces and change notes while an editor walks a tree. Offsets in <see cref="Changes"/> are relative to the list's start.</summary>
internal sealed class PieceList
{
    private readonly List<OutputPiece> _pieces = [];
    private readonly List<WriteChange> _changes = [];

    public IReadOnlyList<OutputPiece> Pieces => _pieces;

    public IReadOnlyList<WriteChange> Changes => _changes;

    public long Length { get; private set; }

    public void AddCopy(long offset, long length)
    {
        if (length <= 0)
        {
            return;
        }

        if (_pieces.Count > 0 && _pieces[^1] is { Kind: OutputPieceKind.Copy } last && last.SourceRange.End == offset)
        {
            _pieces[^1] = OutputPiece.Copy(last.SourceRange.Offset, last.Length + length);
        }
        else
        {
            _pieces.Add(OutputPiece.Copy(offset, length));
        }

        Length += length;
    }

    public void AddData(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        _pieces.Add(OutputPiece.Data(bytes));
        Length += bytes.Length;
    }

    /// <summary>Adds a run of zero bytes that is generated while writing, not held.</summary>
    public void AddZeros(long length)
    {
        if (length > 0)
        {
            AddGenerated(length, new ZeroFill(length));
        }
    }

    /// <summary>Adds a padding element: its header bytes followed by <paramref name="zeros"/> generated zero bytes.</summary>
    public void AddPadding(ReadOnlyMemory<byte> header, long zeros)
    {
        AddData(header);
        AddZeros(zeros);
    }

    public void AddGenerated(long length, IPieceGenerator generator)
    {
        if (length == 0)
        {
            return;
        }

        _pieces.Add(OutputPiece.Generated(length, generator));
        Length += length;
    }

    /// <summary>Appends another list (and its change notes, moved to this list's offsets).</summary>
    public void Add(PieceList other)
    {
        long shift = Length;
        _pieces.EnsureCapacity(_pieces.Count + other._pieces.Count);
        foreach (OutputPiece p in other._pieces)
        {
            if (p.Kind == OutputPieceKind.Copy && p.Length > 0)
            {
                // A copied range that continues the one before it is merged into it; any other is shared as it is (pieces never change).
                if (_pieces.Count > 0 && _pieces[^1] is { Kind: OutputPieceKind.Copy } last && last.SourceRange.End == p.SourceRange.Offset)
                {
                    _pieces[^1] = OutputPiece.Copy(last.SourceRange.Offset, last.Length + p.Length);
                }
                else
                {
                    _pieces.Add(p);
                }

                Length += p.Length;
            }
            else if (p.Kind == OutputPieceKind.Copy)
            {
                continue; // an empty copy adds nothing
            }
            else
            {
                _pieces.Add(p);
                Length += p.Length;
            }
        }

        _changes.EnsureCapacity(_changes.Count + other._changes.Count);
        foreach (WriteChange c in other._changes)
        {
            _changes.Add(c with { Output = new ByteRange(c.Output.Offset + shift, c.Output.Length) });
        }
    }

    public void NoteChange(string description, ByteRange source, long outputStart) =>
        _changes.Add(new WriteChange(description, source, new ByteRange(outputStart, Length - outputStart)));

    public EditPlan ToPlan(long sourceLength, long paddingUsed, long paddingAvailable, long paddingRequired, string? shortfallIn = null) =>
        new([.. _pieces], sourceLength, [.. _changes], paddingUsed, paddingAvailable, paddingRequired, shortfallIn);

    /// <summary>CRC-32 (EBML polynomial) of the bytes this list will produce, streamed so nothing is held.</summary>
    public uint Crc32(IMediaSource source, CancellationToken cancellationToken)
    {
        using CrcSink sink = new();
        PieceWriter.Write(_pieces, source, sink, cancellationToken);
        return EbmlCrc32.Finish(sink.State);
    }
}

/// <summary>A write-only stream that only accumulates the EBML CRC-32.</summary>
internal sealed class CrcSink : Stream
{
    public uint State { get; private set; } = EbmlCrc32.Initial;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => State = EbmlCrc32.Update(State, buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => State = EbmlCrc32.Update(State, buffer);
}

/// <summary>A write-only stream that only counts bytes.</summary>
internal sealed class CountSink : Stream
{
    private long _count;

    public long Count => _count;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _count;

    public override long Position { get => _count; set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _count += count;

    public override void Write(ReadOnlySpan<byte> buffer) => _count += buffer.Length;
}

/// <summary>Runs pieces against a source and a destination.</summary>
internal static class PieceWriter
{
    private const int BufferSize = 256 * 1024;

    /// <summary>Writes the pieces in order to <paramref name="output"/>. Memory use is one buffer, whatever the file size.</summary>
    public static (long Copied, long Written) Write(IReadOnlyList<OutputPiece> pieces, IMediaSource source, Stream output, CancellationToken cancellationToken)
    {
        long copied = 0;
        long written = 0;
        byte[]? buffer = null;
        try
        {
            foreach (OutputPiece piece in pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (piece.Kind)
                {
                    case OutputPieceKind.Copy:
                        // A source held in memory needs no staging buffer, so none is rented for it (a 256 KiB rent per call showed up when a small CRC-32 was computed over a few pieces).
                        if (source is not MemoryMediaSource)
                        {
                            buffer ??= System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
                        }

                        CopyRange(source, piece.SourceRange, output, buffer, cancellationToken);
                        copied += piece.Length;
                        break;
                    case OutputPieceKind.Data:
                        output.Write(piece.Bytes.Span);
                        written += piece.Length;
                        break;
                    default:
                        CountingStream counting = new(output);
                        piece.Generator!.Write(counting, source, cancellationToken);
                        if (counting.Count != piece.Length)
                        {
                            throw new InvalidOperationException($"A generated piece wrote {counting.Count} byte(s) but declared {piece.Length}.");
                        }

                        written += piece.Length;
                        break;
                }
            }
        }
        finally
        {
            if (buffer is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return (copied, written);
    }

    /// <summary>Copies a range of the source to a stream in constant memory.</summary>
    public static void CopyFrom(IMediaSource source, long offset, long length, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            CopyRange(source, new ByteRange(offset, length), output, buffer, cancellationToken);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CopyRange(IMediaSource source, ByteRange range, Stream output, byte[]? buffer, CancellationToken cancellationToken)
    {
        // A source held in memory hands its bytes straight to the output: one copy instead of two.
        if (source is MemoryMediaSource memory && memory.TryGetRange(range.Offset, range.Length, out ReadOnlyMemory<byte> bytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(bytes.Span);
            return;
        }

        if (buffer is null)
        {
            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024); // a memory source whose range was refused (past its end): take the ordinary path
            try
            {
                CopyRange(source, range, output, buffer, cancellationToken);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }

            return;
        }

        long pos = range.Offset;
        long remaining = range.Length;
        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, buffer.Length);
            int n = source.Read(pos, buffer.AsSpan(0, want), cancellationToken);
            if (n <= 0)
            {
                throw new IOException($"The source ended at {pos} while copying; the file changed after the edit was planned.");
            }

            output.Write(buffer, 0, n);
            pos += n;
            remaining -= n;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Count += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Count += buffer.Length;
        }
    }
}
