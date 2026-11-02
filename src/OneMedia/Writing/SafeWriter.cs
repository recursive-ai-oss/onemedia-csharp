namespace RecursiveAI.OneMedia;

/// <summary>
/// Applies an <see cref="EditPlan"/> to a file safely. Three ways, in order of how much they promise:
/// <see cref="SaveAs"/> writes a new file and leaves the original alone; <see cref="Save"/> writes a new file next to the original and
/// atomically replaces it, so an interrupted save leaves the original intact; <see cref="SaveInPlace"/> overwrites only the changed ranges
/// and only when the plan keeps every other byte where it is, otherwise it refuses with <see cref="PaddingRequiredException"/>.
/// </summary>
/// <remarks>
/// Memory use is a few buffers whatever the file size. Media payload is copied by range, never held. A new file is written under a
/// temporary name in the destination's folder (<c>.name.onemedia-&lt;host&gt;-&lt;process id&gt;-&lt;random&gt;.tmp</c>), flushed to disk and then renamed, so the target name
/// never shows a partly written file. A process killed mid-write can leave the temporary file behind; it is never the target and is safe to
/// delete, and the next save of the same target removes it (the process that made it is gone, or it is a day old). An in-place write is the one operation that is not atomic: a crash during it can leave a metadata range half written, which is
/// why it only touches the ranges the plan names and never media payload.
/// </remarks>
public static class SafeWriter
{
    private const int FileBuffer = 128 * 1024;

    /// <summary>Writes the new file to any seekable or non-seekable stream. The original is read through <paramref name="source"/>.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="source">The original file the plan was made for.</param>
    /// <param name="destination">Where the new file goes.</param>
    /// <param name="cancellationToken">Cancels the write; the destination then holds a partial file.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="IOException">The source's length differs from the length the plan was made for, or reading or writing failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport Write(EditPlan plan, IMediaSource source, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        CheckSource(plan, source);
        (long copied, long written) = PieceWriter.Write(plan.Pieces, source, destination, cancellationToken);
        return new WriteReport(WriteMode.SaveAs, plan, copied, written);
    }

    /// <summary>Writes the edited file to a new path and leaves the original untouched.</summary>
    /// <param name="plan">The plan, made for the file at <paramref name="sourcePath"/>.</param>
    /// <param name="sourcePath">The original file.</param>
    /// <param name="destinationPath">The new file. Must differ from the original (use <see cref="Save"/> to replace it).</param>
    /// <param name="overwrite">Replace the destination when it exists; by default an existing destination is an error.</param>
    /// <param name="cancellationToken">Cancels the write; the temporary file is deleted.</param>
    /// <returns>The report, with <see cref="WriteReport.Mode"/> <see cref="WriteMode.SaveAs"/>.</returns>
    /// <exception cref="ArgumentException">The destination is the source file.</exception>
    /// <exception cref="IOException">The destination exists and <paramref name="overwrite"/> is false, the source changed since planning, or the disk failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport SaveAs(EditPlan plan, string sourcePath, string destinationPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        string full = Path.GetFullPath(destinationPath);
        if (string.Equals(full, Path.GetFullPath(sourcePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The destination is the source file; use Save to replace it.", nameof(destinationPath));
        }

        if (!overwrite && File.Exists(full))
        {
            throw new IOException($"'{destinationPath}' already exists.");
        }

        return WriteViaTemp(plan, sourcePath, full, WriteMode.SaveAs, copyMode: false, overwrite, cancellationToken);
    }

    /// <summary>
    /// Replaces the file with the edited version atomically: the new file is written next to it under a temporary name, flushed, and renamed over
    /// the original. Until the rename the original is complete and unchanged, so a crash or kill at any point leaves it intact.
    /// </summary>
    /// <param name="plan">The plan, made for the file at <paramref name="path"/>.</param>
    /// <param name="path">The file to replace.</param>
    /// <param name="cancellationToken">Cancels the write; the temporary file is deleted and the original stays.</param>
    /// <returns>The report, with <see cref="WriteReport.Mode"/> <see cref="WriteMode.Save"/>.</returns>
    /// <exception cref="IOException">The file changed since planning, or the disk failed.</exception>
    /// <exception cref="FileInUseException">On Windows, another open handle (such as a <see cref="MediaFile"/> on the same path) prevents the replacement; the original is untouched. Dispose the reader and save again, or use <see cref="SaveAs"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport Save(EditPlan plan, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(path);
        // A symbolic link is followed: the edit goes to the file it points to and the link stays a link. Renaming over the link itself
        // would replace the link with a regular file and leave the real file unedited.
        string full = Path.GetFullPath(path);
        string resolved = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        return WriteViaTemp(plan, resolved, resolved, WriteMode.Save, copyMode: true, overwrite: true, cancellationToken);
    }

    /// <summary>
    /// Overwrites only the changed ranges of the file, when the plan keeps every other byte where it is
    /// (<see cref="EditPlan.IsSameLayout"/>): new content fits in existing padding and nothing moved. Otherwise nothing is written.
    /// </summary>
    /// <param name="plan">The plan, made for the file at <paramref name="path"/>.</param>
    /// <param name="path">The file to edit.</param>
    /// <param name="cancellationToken">Cancels before the first write; once writing has started it runs to the end.</param>
    /// <returns>The report, with <see cref="WriteReport.Mode"/> <see cref="WriteMode.InPlace"/> and <see cref="WriteReport.BytesCopied"/> 0.</returns>
    /// <exception cref="PaddingRequiredException">The edit does not fit in the padding the file has; the file is untouched.</exception>
    /// <exception cref="IOException">The file changed since planning, or the disk failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport SaveInPlace(EditPlan plan, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!plan.IsSameLayout)
        {
            throw new PaddingRequiredException(Math.Max(1, plan.PaddingRequired), plan.PaddingAvailable, plan.ShortfallIn);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using FileMediaSource source = FileMediaSource.Open(path);
        CheckSource(plan, source);
        // Two passes. The first compares every new piece with what the file holds and notes the runs of bytes that differ (nothing is written, so
        // it can still be cancelled). The second writes just those runs, one after the other, with no reads in between: the file is half
        // changed only for as long as it takes to write the changed bytes, which is the shortest window the operation can have.
        ChangedRuns runs = new();
        long at = 0;
        foreach (OutputPiece piece in plan.Pieces)
        {
            if (piece.Kind != OutputPieceKind.Copy)
            {
                using FindChangedRunsStream find = new(source, at, runs);
                PieceWriter.Write([piece], source, find, cancellationToken);
            }

            at += piece.Length;
        }

        long written = 0;
        if (runs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No write buffer: every write goes straight to the file.
            using FileStream file = new(path, FileMode.Open, FileAccess.Write, FileShare.Read, 0);
            at = 0;
            foreach (OutputPiece piece in plan.Pieces)
            {
                if (piece.Kind != OutputPieceKind.Copy)
                {
                    using WriteChangedRunsStream write = new(file, at, runs);
                    PieceWriter.Write([piece], source, write, CancellationToken.None);
                    written += write.BytesWritten;
                }

                at += piece.Length;
            }

            file.Flush(true);
        }

        return new WriteReport(WriteMode.InPlace, plan, 0, written);
    }

    private static WriteReport WriteViaTemp(EditPlan plan, string sourcePath, string target, WriteMode mode, bool copyMode, bool overwrite, CancellationToken cancellationToken)
    {
        string dir = Path.GetDirectoryName(target) ?? ".";
        RemoveStaleTemporaryFiles(dir, Path.GetFileName(target));
        string temp = Path.Combine(dir, TemporaryName(Path.GetFileName(target)));
        bool done = false;
        try
        {
            long copied;
            long written;
            // The source handle is closed before the rename: Windows cannot replace a file while any handle to it is open, our own included.
            using (FileMediaSource source = FileMediaSource.Open(sourcePath))
            {
                CheckSource(plan, source);
                using FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileBuffer);
                (copied, written) = PieceWriter.Write(plan.Pieces, source, output, cancellationToken);
                output.Flush(true);
            }

            if (copyMode && !OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
            {
                // The replacement keeps the original's permissions.
                File.SetUnixFileMode(temp, File.GetUnixFileMode(sourcePath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temp, target, overwrite);
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException && File.Exists(target))
            {
                // Windows refuses to replace a file another handle (for example a MediaFile opened on the same path) still holds.
                // Nothing has changed: the original is complete and the temporary file is removed below.
                throw new FileInUseException(target, e);
            }

            done = true;
            return new WriteReport(mode, plan, copied, written);
        }
        finally
        {
            if (!done)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // The temporary file is never the target; leaving it behind is harmless.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same: nothing to do.
                }
            }
        }
    }

    // The temporary file of a write is named <dot><target name>.onemedia-<4 hex digits of the host name>-<process id in hex>-<8 random hex digits>.tmp, so that
    // the next save of the same target can recognise what an earlier, killed save left behind, and prove that it was its own. The host digits keep a save on
    // one machine from judging the live temporary file of another machine that shares the folder.
    private const string TempMarker = ".onemedia-";
    private const string TempSuffix = ".tmp";

    private static string TemporaryName(string targetName) =>
        $".{targetName}{TempMarker}{HostTag():x4}-{Environment.ProcessId:x}-{Guid.NewGuid().ToString("N")[..8]}{TempSuffix}";

    private static int HostTag()
    {
        int h = 17;
        foreach (char c in Environment.MachineName)
        {
            h = unchecked((h * 31) + c);
        }

        return h & 0xFFFF;
    }

    /// <summary>How old a temporary file must be before another save may remove it when the process that made it cannot be shown to be gone.</summary>
    internal static TimeSpan StaleAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes temporary files that earlier saves of this target left behind when they were killed. A file is removed only when its name is exactly
    /// that of a temporary file for this target and either the process that made it is gone, or it was last written more than <see cref="StaleAge"/> ago.
    /// Failures are ignored: a leftover file does no harm, and a save must not fail because of another save's debris.
    /// </summary>
    private static void RemoveStaleTemporaryFiles(string directory, string targetName)
    {
        string prefix = $".{targetName}{TempMarker}";
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*" + TempSuffix))
            {
                if (!IsStaleTemporaryName(Path.GetFileName(candidate), prefix, out int hostTag, out int processId))
                {
                    continue;
                }

                bool sameHost = hostTag == HostTag();
                bool owned = sameHost && processId != Environment.ProcessId && !ProcessExists(processId);
                bool old = DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) > StaleAge;
                if (owned || old)
                {
                    File.Delete(candidate);
                }
            }
        }
        catch (IOException)
        {
            // Debris cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    // <prefix><4 hex digits>-<hex process id>-<8 hex digits>.tmp, and nothing else.
    private static bool IsStaleTemporaryName(string name, string prefix, out int hostTag, out int processId)
    {
        hostTag = 0;
        processId = 0;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(TempSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> middle = name.AsSpan(prefix.Length, name.Length - prefix.Length - TempSuffix.Length);
        int first = middle.IndexOf('-');
        int last = middle.LastIndexOf('-');
        if (first != 4 || last <= first + 1 || middle.Length - last - 1 != 8)
        {
            return false;
        }

        foreach (char c in middle[(last + 1)..])
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        System.Globalization.NumberStyles hex = System.Globalization.NumberStyles.AllowHexSpecifier;
        return int.TryParse(middle[..first], hex, System.Globalization.CultureInfo.InvariantCulture, out hostTag)
            && int.TryParse(middle[(first + 1)..last], hex, System.Globalization.CultureInfo.InvariantCulture, out processId) && processId > 0;
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(processId);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return true; // cannot tell (browser): fall back to the age rule
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // cannot tell: assume it is alive
        }
    }

    private static void CheckSource(EditPlan plan, IMediaSource source)
    {
        if (source.Length != plan.SourceLength)
        {
            throw new IOException($"The source is {source.Length} bytes but the plan was made for {plan.SourceLength}; the file changed after it was read.");
        }
    }
}

/// <summary>The runs of bytes (position and length) an in-place write has to change, in increasing position order.</summary>
internal sealed class ChangedRuns
{
    // A bound on what a hostile plan can make us hold: past it, neighbouring runs are joined, which writes a few unchanged bytes between them.
    private const int MaxRuns = 1 << 16;
    private readonly List<(long Start, long Length)> _runs = [];

    public int Count => _runs.Count;

    public (long Start, long Length) this[int index] => _runs[index];

    public void Add(long start, long length)
    {
        if (_runs.Count > 0 && _runs[^1].Start + _runs[^1].Length == start)
        {
            _runs[^1] = (_runs[^1].Start, _runs[^1].Length + length);
            return;
        }

        _runs.Add((start, length));
        if (_runs.Count >= MaxRuns)
        {
            Compact();
        }
    }

    // Joins each run to the next one, halving the list.
    private void Compact()
    {
        List<(long Start, long Length)> joined = new(_runs.Count / 2 + 1);
        for (int i = 0; i < _runs.Count; i += 2)
        {
            (long start, long length) = _runs[i];
            if (i + 1 < _runs.Count)
            {
                length = _runs[i + 1].Start + _runs[i + 1].Length - start;
            }

            joined.Add((start, length));
        }

        _runs.Clear();
        _runs.AddRange(joined);
    }
}

/// <summary>
/// A write-only stream that, for each block written at a position, reads the same range of the original and records the runs of bytes that
/// differ (runs closer than <see cref="MergeGap"/> bytes are joined). It writes nothing.
/// </summary>
internal sealed class FindChangedRunsStream(IMediaSource original, long start, ChangedRuns runs) : Stream
{
    private const int Block = 64 * 1024;
    private const int MergeGap = 32;
    private readonly byte[] _old = System.Buffers.ArrayPool<byte>.Shared.Rent(Block);
    private long _position = start;
    private bool _disposed;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            ReadOnlySpan<byte> chunk = buffer[..Math.Min(buffer.Length, Block)];
            int have = original.Read(_position, _old.AsSpan(0, chunk.Length), CancellationToken.None);
            Find(chunk, Math.Max(0, have));
            _position += chunk.Length;
            buffer = buffer[chunk.Length..];
        }
    }

    // Bytes past `have` have no counterpart in the original (cannot happen for a same-layout plan) and count as different.
    private void Find(ReadOnlySpan<byte> chunk, int have)
    {
        int n = chunk.Length;
        int i = 0;
        while (i < n)
        {
            if (i < have)
            {
                i += MemoryExtensions.CommonPrefixLength(chunk[i..have], _old.AsSpan(i, have - i));
                if (i >= n)
                {
                    break;
                }
            }

            int runStart = i;
            int lastDifferent = i;
            i++;
            while (i < n && i - lastDifferent < MergeGap)
            {
                if (i >= have || chunk[i] != _old[i])
                {
                    lastDifferent = i;
                }

                i++;
            }

            runs.Add(_position + runStart, lastDifferent - runStart + 1);
            i = lastDifferent + 1;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            System.Buffers.ArrayPool<byte>.Shared.Return(_old);
        }

        base.Dispose(disposing);
    }
}

/// <summary>A write-only stream over an open file that writes only the parts of what it is given that fall in the recorded runs.</summary>
internal sealed class WriteChangedRunsStream(FileStream file, long start, ChangedRuns runs) : Stream
{
    private long _position = start;
    private int _next; // the first run that may still overlap the data to come

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        long end = _position + buffer.Length;
        // Runs before this data were handled by earlier pieces (runs are in position order); skip any that end before it.
        while (_next < runs.Count && runs[_next].Start + runs[_next].Length <= _position)
        {
            _next++;
        }

        int i = _next;
        while (i < runs.Count && runs[i].Start < end)
        {
            (long runStart, long runLength) = runs[i];
            long from = Math.Max(runStart, _position);
            long to = Math.Min(runStart + runLength, end);
            if (to > from)
            {
                file.Position = from;
                file.Write(buffer.Slice((int)(from - _position), (int)(to - from)));
                BytesWritten += to - from;
            }

            if (runStart + runLength > end)
            {
                break; // the run goes on into the next block
            }

            i++;
        }

        _next = i;
        _position = end;
    }
}

/// <summary>
/// The target of a replacing save is held open by another handle and the operating system (Windows) does not allow it to be replaced.
/// The original file is complete and unchanged, and no temporary file is left behind.
/// </summary>
public sealed class FileInUseException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="path">The file that could not be replaced.</param>
    /// <param name="inner">The operating system's error.</param>
    public FileInUseException(string path, Exception inner)
        : base($"'{path}' is open elsewhere and cannot be replaced; close it and save again, or save to a new file.", inner)
    {
        Path = path;
    }

    /// <summary>The file that could not be replaced.</summary>
    public string Path { get; }
}
