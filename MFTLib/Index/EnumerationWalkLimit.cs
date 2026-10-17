namespace MFTLib.Index;

/// <summary>
///     The process-wide limit on directory-tree walks: at most one walk per processor at a time,
///     across every <see cref="FileIndex" /> in the process and every drive of each. An MFT scan
///     is bounded by the broker's parse-thread allocator; an enumeration walk never reaches the
///     broker, so this limit is what keeps the bound on scans true wherever drives are scanned.
/// </summary>
internal static class EnumerationWalkLimit
{
    static readonly int s_size = Environment.ProcessorCount;
    static readonly SemaphoreSlim s_walks = new(s_size);

    internal static readonly WalkHooks Hooks = new();

    /// <summary>Process-global test hooks for enumeration walk queueing.</summary>
    internal sealed class WalkHooks
    {
        /// <summary>Invoked when a walk is about to queue behind the limit.</summary>
        internal Action? WalkQueued;

        internal void ResetToDefaults() => WalkQueued = null;
    }

    /// <summary>Waits for a walk slot; disposing the lease returns it.</summary>
    internal static async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        var entering = s_walks.WaitAsync(cancellationToken);
        if (!entering.IsCompleted)
        {
            Hooks.WalkQueued?.Invoke();
        }

        await entering.ConfigureAwait(false);
        return new Lease();
    }

    internal readonly struct Lease : IDisposable
    {
        public void Dispose() => s_walks.Release();
    }
}
