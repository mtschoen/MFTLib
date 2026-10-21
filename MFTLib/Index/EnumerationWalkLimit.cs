namespace MFTLib.Index;

/// <summary>
///     The process-wide directory-reader budget: one permit per processor, shared by base walks
///     and their opportunistic workers across every <see cref="FileIndex" /> and drive. An MFT scan
///     is bounded by the broker's parse-thread allocator; an enumeration walk never reaches the
///     broker, so this limit is what keeps the bound on scans true wherever drives are scanned.
/// </summary>
internal static class EnumerationWalkLimit
{
    static readonly int s_size = Environment.ProcessorCount;
    static readonly SemaphoreSlim s_walks = new(s_size, s_size);

    internal static readonly WalkHooks Hooks = new();

    /// <summary>Process-global test hooks for enumeration walk queueing.</summary>
    internal sealed class WalkHooks
    {
        /// <summary>Invoked when a walk is about to queue behind the limit.</summary>
        internal Action? WalkQueued;

        /// <summary>Invoked before physical reading begins for a directory.</summary>
        internal Action<DriveKey, string, CancellationToken>? DirectoryEntered;

        /// <summary>Invoked after physical reading completes or faults for a directory.</summary>
        internal Action<DriveKey, string>? DirectoryExited;

        internal void ResetToDefaults()
        {
            WalkQueued = null;
            DirectoryEntered = null;
            DirectoryExited = null;
        }
    }

    /// <summary>Waits for a walk slot; disposing the lease returns it.</summary>
    internal static async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entering = s_walks.WaitAsync(cancellationToken);
        if (!entering.IsCompleted)
        {
            Hooks.WalkQueued?.Invoke();
        }

        try
        {
            await entering.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new Lease();
    }

    internal static Lease? TryEnter() => s_walks.Wait(0) ? new Lease() : null;

    internal sealed class Lease : IDisposable
    {
        int _released;

        internal void ThrowIfReleased() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                s_walks.Release();
            }
        }
    }
}
