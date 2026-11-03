using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Input;

/// <summary>The common shape of the sequential readers (Ogg, FLAC, FLV): asynchronous at the bottom, so one engine serves the synchronous and the asynchronous demuxer.</summary>
internal interface ISequentialEngine : IDisposable
{
    IReadOnlyList<MediaTrack> Tracks { get; }

    MediaTime Duration { get; }

    DefectLog Defects { get; }

    bool CanSeek { get; }

    PacketView Current { get; }

    void SetTrackEnabled(int trackIndex, bool enabled);

    ValueTask OpenAsync(CancellationToken ct);

    ValueTask<bool> NextAsync(CancellationToken ct);

    ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode, CancellationToken ct);

    MediaMetadata ReadMetadata();
}

internal static class EngineHost
{
    /// <summary>Completes a task that is already complete without blocking; a source that really waits is waited for (a synchronous caller asked for that).</summary>
    /// <exception cref="NotSupportedException">The task is not complete and the host is single-threaded (browser WebAssembly), where nothing can finish it while the caller waits.</exception>
    public static T Wait<T>(ValueTask<T> task) => task.IsCompletedSuccessfully ? task.Result : WaitSlow(task);

    public static void Wait(ValueTask task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        // A task that finished with an error is complete: getting its result rethrows the error, which is what the caller must see.
        if (!task.IsCompleted && OperatingSystem.IsBrowser())
        {
            throw CannotBlock();
        }

        task.AsTask().GetAwaiter().GetResult();
    }

    private static T WaitSlow<T>(ValueTask<T> task)
    {
        if (!task.IsCompleted && OperatingSystem.IsBrowser())
        {
            throw CannotBlock();
        }

        return task.AsTask().GetAwaiter().GetResult();
    }

    // The browser runtime has one thread: a wait for a read that has not finished could never end, and the runtime's own error says only that
    // monitors cannot be waited on. Readers written against synchronous sources reach this when they are given a source that answers later.
    private static NotSupportedException CannotBlock() => new(
        "This reader reads through synchronous code, and the source did not answer at once. A single-threaded host (browser WebAssembly) cannot wait for it; " +
        "give the reader a source that answers immediately (bytes already in memory, or a file in the virtual file system), or use a format with an asynchronous reader (ISO BMFF, Matroska, ASF, Ogg, WAV, AIFF, CAF, MP3, ADTS, AC-3, AMR).");

    /// <summary>Opens an engine over a synchronous source; the source is disposed when opening fails.</summary>
    public static T Open<T>(T engine, IDisposable? owned, CancellationToken ct) where T : ISequentialEngine
    {
        try
        {
            Wait(engine.OpenAsync(ct));
            return engine;
        }
        catch
        {
            engine.Dispose();
            owned?.Dispose();
            throw;
        }
    }

    /// <summary>Opens an engine over an asynchronous source; the source is disposed when opening fails.</summary>
    public static async ValueTask<T> OpenAsync<T>(T engine, IAsyncDisposable? owned, CancellationToken ct) where T : ISequentialEngine
    {
        try
        {
            await engine.OpenAsync(ct).ConfigureAwait(false);
            return engine;
        }
        catch
        {
            engine.Dispose();
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
}
