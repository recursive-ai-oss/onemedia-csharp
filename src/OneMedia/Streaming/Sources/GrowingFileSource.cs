namespace RecursiveAI.OneMedia.Streaming.Sources;

/// <summary>
/// How long a reader of something that is still being written (a growing file, a live playlist) waits at the current end for more, and when it
/// gives up. The caller holds the control: the wait observes the caller's <see cref="CancellationToken"/>, ends after
/// <see cref="IdleTimeoutMilliseconds"/> without new data, and ends at once, after one last look, when <see cref="IsComplete"/> returns true.
/// </summary>
public sealed class GrowthPolicy
{
    /// <summary>Milliseconds between looks for more data. Default 100.</summary>
    public int PollIntervalMilliseconds { get; init; } = 100;

    /// <summary>
    /// Milliseconds without any new data after which the end is taken to be the end. Default 10000. <see cref="Timeout.Infinite"/> (-1) never gives up:
    /// then only <see cref="IsComplete"/> or cancellation ends the wait.
    /// </summary>
    public int IdleTimeoutMilliseconds { get; init; } = 10_000;

    /// <summary>
    /// Asked at every poll that found nothing: return true once the writer is known to be finished (a marker file exists, the encoder's process
    /// has exited). The reader then takes one more look and, if there is still nothing, ends. Null means the idle timeout is the only stop.
    /// </summary>
    public Func<bool>? IsComplete { get; init; }

    /// <summary>A policy that does not wait: the current end is the end. Use it to read what is there now.</summary>
    public static GrowthPolicy NoWait { get; } = new() { IdleTimeoutMilliseconds = 0 };

    internal Episode Begin() => new(this);

    /// <summary>One wait at the end of the data: it starts when a read finds nothing and is dropped when a read finds something.</summary>
    internal sealed class Episode(GrowthPolicy policy)
    {
        private readonly long _started = Environment.TickCount64;
        private bool _finalLook;

        /// <summary>Sleeps one poll interval. Returns false when the reader should stop waiting and report the end.</summary>
        public bool Wait(CancellationToken cancellationToken)
        {
            if (!ShouldContinue())
            {
                return false;
            }

            // WaitOne returns true when the token was cancelled during the sleep.
            if (cancellationToken.WaitHandle.WaitOne(Math.Max(policy.PollIntervalMilliseconds, 1)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return true;
        }

        /// <summary>Asynchronous <see cref="Wait"/>.</summary>
        public async ValueTask<bool> WaitAsync(CancellationToken cancellationToken)
        {
            if (!ShouldContinue())
            {
                return false;
            }

            await Task.Delay(Math.Max(policy.PollIntervalMilliseconds, 1), cancellationToken).ConfigureAwait(false);
            return true;
        }

        private bool ShouldContinue()
        {
            if (_finalLook)
            {
                return false;
            }

            if (policy.IsComplete?.Invoke() == true)
            {
                // The writer says it is done: one more read, without sleeping, catches what it wrote last.
                _finalLook = true;
                return true;
            }

            if (policy.IdleTimeoutMilliseconds >= 0 && Environment.TickCount64 - _started >= policy.IdleTimeoutMilliseconds)
            {
                return false;
            }

            return true;
        }
    }
}

/// <summary>
/// A forward-only source over a file that another process is still appending to (a fragmented MP4, a live Matroska, a transport stream or an
/// FLV being recorded). Reads return what is on disk; at the current end the source waits for more under its <see cref="GrowthPolicy"/>, and
/// reports the end of the data only when the policy gives up or says the writer is done. The demuxer on top therefore reaches the current end, waits,
/// and carries on, with memory that does not depend on how long the recording runs.
/// </summary>
/// <remarks>
/// A progressive MP4 or MOV whose movie box is written after the media data cannot be read before it is finished: opening one
/// (<see cref="Media.OpenGrowing(string, GrowthPolicy, CancellationToken)"/>) reports it with a <see cref="MediaFormatException"/> whose code is <c>isobmff.moov-not-written</c>.
/// The file is read from its first byte; a file that is replaced or truncated underneath the reader ends the data.
/// </remarks>
public sealed class GrowingFileSource : IForwardMediaSource
{
    private readonly FileStream _stream;
    private readonly GrowthPolicy _policy;
    private long _position;

    private GrowingFileSource(FileStream stream, GrowthPolicy policy, string path)
    {
        _stream = stream;
        _policy = policy;
        Path = path;
    }

    /// <summary>The file being read.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public long Position => _position;

    /// <summary>
    /// Opens the file for reading while a writer has it open. If it does not exist yet the call waits for it to appear under the policy.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="policy">When to wait and when to stop.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The source; dispose it to close the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="FileNotFoundException">The file did not appear before the policy gave up.</exception>
    public static GrowingFileSource Open(string path, GrowthPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(policy);
        GrowthPolicy.Episode? wait = null;
        while (true)
        {
            try
            {
                FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                return new GrowingFileSource(fs, policy, path);
            }
            catch (FileNotFoundException)
            {
                wait ??= policy.Begin();
                if (!wait.Wait(cancellationToken))
                {
                    throw;
                }
            }
            catch (DirectoryNotFoundException)
            {
                wait ??= policy.Begin();
                if (!wait.Wait(cancellationToken))
                {
                    throw;
                }
            }
        }
    }

    /// <inheritdoc />
    public int Read(Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        GrowthPolicy.Episode? wait = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = _stream.Read(destination);
            if (n > 0)
            {
                _position += n;
                return n;
            }

            wait ??= _policy.Begin();
            if (!wait.Wait(cancellationToken))
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        GrowthPolicy.Episode? wait = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (n > 0)
            {
                _position += n;
                return n;
            }

            wait ??= _policy.Begin();
            if (!await wait.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
