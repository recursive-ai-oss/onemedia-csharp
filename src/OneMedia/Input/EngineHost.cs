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
    public static T Wait<T>(ValueTask<T> task) => task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

    public static void Wait(ValueTask task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        task.AsTask().GetAwaiter().GetResult();
    }

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
