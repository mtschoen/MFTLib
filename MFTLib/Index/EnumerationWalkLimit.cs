namespace MFTLib.Index;

/// <summary>
///     The process-wide directory-reader budget: one permit per processor, shared by base walks
///     and their opportunistic workers across every <see cref="FileIndex" /> and drive. An MFT scan
///     is bounded by the broker's parse-thread allocator; an enumeration walk never reaches the
///     broker, so this limit is what keeps the bound on scans true wherever drives are scanned.
/// </summary>
static class EnumerationWalkLimit
{
    static readonly int Capacity = Environment.ProcessorCount;
    static readonly SemaphoreSlim Walks = new(Capacity, Capacity);

    internal static readonly WalkHooks Hooks = new();

    /// <summary>Process-global test hooks for enumeration walk queueing.</summary>
    internal sealed class WalkHooks
    {
        /// <summary>Invoked when a walk is about to queue behind the limit.</summary>
        internal Action? _walkQueued;

        /// <summary>Invoked before physical reading begins for a directory.</summary>
        internal Action<RootKey, string, CancellationToken>? _directoryEntered;

        /// <summary>Invoked after physical reading completes or faults for a directory.</summary>
        internal Action<RootKey, string>? _directoryExited;

        internal void ResetToDefaults()
        {
            _walkQueued = null;
            _directoryEntered = null;
            _directoryExited = null;
        }
    }

    /// <summary>Waits for a walk slot; disposing the lease returns it.</summary>
    internal static async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entering = Walks.WaitAsync(cancellationToken);
        if (!entering.IsCompleted)
        {
            Hooks._walkQueued?.Invoke();
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

    internal static Lease? TryEnter() => Walks.Wait(0) ? new Lease() : null;

    internal sealed class Lease : IDisposable
    {
        int _released;

        internal void ThrowIfReleased() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Walks.Release();
            }
        }
    }
}
