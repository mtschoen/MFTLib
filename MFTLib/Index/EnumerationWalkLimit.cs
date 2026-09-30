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

    /// <summary>
    ///     A test seam: invoked when a walk finds every slot taken and is about to wait, so a test
    ///     can order itself against the queue without sleeping.
    /// </summary>
    internal static Action? WalkQueuedForTest { get; set; }

    /// <summary>Waits for a walk slot; disposing the lease returns it.</summary>
    internal static async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        var entering = s_walks.WaitAsync(cancellationToken);
        if (!entering.IsCompleted)
        {
            WalkQueuedForTest?.Invoke();
        }

        await entering.ConfigureAwait(false);
        return new Lease();
    }

    /// <summary>
    ///     Shrinks the limit to <paramref name="size" /> walks (at most the processor count) until
    ///     the returned scope is disposed, by holding back the slots above it. The limit is
    ///     process-wide, so a test that resizes it is not parallelizable.
    /// </summary>
    internal static async Task<IDisposable> OverrideSizeForTestAsync(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, s_size);
        var heldBack = s_size - size;
        for (var slot = 0; slot < heldBack; slot++)
        {
            await s_walks.WaitAsync().ConfigureAwait(false);
        }

        return new SizeOverride(heldBack);
    }

    internal readonly struct Lease : IDisposable
    {
        public void Dispose() => s_walks.Release();
    }

    sealed class SizeOverride(int heldBack) : IDisposable
    {
        public void Dispose()
        {
            if (heldBack > 0)
            {
                s_walks.Release(heldBack);
            }
        }
    }
}
