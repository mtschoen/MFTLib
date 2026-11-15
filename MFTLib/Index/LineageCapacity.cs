namespace MFTLib.Index;

/// <summary>
///     What one block lineage remembers about changes it could not store (MFTLib issue 953, "Cannot grow"): each name
///     region found at its clamp, so a later refusal by that region skips without another growth attempt, and whether
///     the lineage has already raised <see cref="WatchFaultKind.CapacityExhausted" />. A rescan starts a new lineage, which
///     remembers nothing. A growth's I/O failure is not remembered here: it ends growing for its own batch only, and the
///     next refusal in a later batch tries again. A slot plan past the largest plannable slot region is not remembered
///     either, because it belongs to its record: a smaller record past the slot capacity can still grow the block.
/// </summary>
sealed class LineageCapacity
{
    /// <summary>The clamp of each name region found at it, written and read only under the root's write gate.</summary>
    readonly Dictionary<WriteRefusal, Exception> _clamps = [];

    int _notified;

    /// <summary>The cause recorded for <paramref name="reason" />'s region at its clamp, or null when it can still grow.</summary>
    /// <remarks>The caller holds the root's write gate.</remarks>
    internal Exception? ClampFor(WriteRefusal reason) => _clamps.GetValueOrDefault(reason);

    /// <summary>Records that <paramref name="reason" />'s region is at its clamp, with the planner's exception as the cause.</summary>
    /// <remarks>The caller holds the root's write gate.</remarks>
    internal void RememberClamp(WriteRefusal reason, Exception clamp)
    {
        ArgumentNullException.ThrowIfNull(clamp);
        _clamps.TryAdd(reason, clamp);
    }

    /// <summary>True exactly once per lineage: the caller then owns raising the lineage's one notification.</summary>
    internal bool TryClaimNotification() => Interlocked.Exchange(ref _notified, 1) == 0;
}
