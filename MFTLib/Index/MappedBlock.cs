using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     I6 Mapping: one block lineage, shared by every view's RootBlock. It publishes one
///     <see cref="BlockMapping" /> at a time as <see cref="Current" />, so a single volatile store moves every view
///     onto a replacement mapping while handle identity, <see cref="ProducerKind" /> and
///     <see cref="ReportedCycles" /> stay with the lineage. A rescan starts a new lineage. Scan teardown, retirement
///     draining or finalization releases its mappings; closing an individual view never releases them.
/// </summary>
/// <remarks>
///     A mapping that <see cref="Replace" /> retired stays mapped until the lineage itself is released, because an
///     unborrowed <see cref="FileEntry" /> read can still be inside it and takes no count that a release could wait
///     for. Its file is deleted then, when its release has a scheduled delete.
/// </remarks>
sealed class MappedBlock
{
    /// <summary>Guards <see cref="_retired" />, <see cref="_isSealed" /> and replacement of <see cref="_current" />.</summary>
    readonly Lock _gate = new();

    /// <summary>Every mapping this lineage replaced, held strongly so none is finalized while its lineage lives.</summary>
    readonly List<BlockMapping> _retired = [];

    BlockMapping _current;

    /// <summary>Set when the lineage's mappings are handed to a release; no replacement is published after it.</summary>
    bool _isSealed;

    internal MappedBlock(BlockFile block)
    {
        _current = new BlockMapping(block);
        ProducerKind = block.Header.ProducerKind;
    }

    /// <summary>The mapping a reader admitted now reads. Read once per operation and pinned for its length.</summary>
    internal BlockMapping Current => Volatile.Read(ref _current);

    /// <summary>The current mapping's block, for a caller that holds the write gate, where it cannot move.</summary>
    internal BlockFile Block => Current.Block;

    /// <summary>The current mapping's release state.</summary>
    internal RootBlockRelease ReleaseState => Current.ReleaseState;

    internal ProducerKind ProducerKind { get; }
    internal ReportedReasonCycles ReportedCycles { get; } = new();

    /// <summary>The lineage's memory of changes it could not store; a rescan's new lineage starts clean.</summary>
    internal LineageCapacity Capacity { get; } = new();

    internal LineageHooks Hooks { get; } = new();

    /// <summary>Per-instance test hooks for this lineage.</summary>
    internal sealed class LineageHooks
    {
        /// <summary>Invoked by <see cref="TryTakeBorrow" /> after it counted a borrow, before it rechecks the current mapping.</summary>
        internal Action? _borrowCounted;
    }

    /// <summary>True from the moment the current mapping's release begins, which is the lineage's release.</summary>
    internal bool IsReleased => Current.IsReleased;

    /// <summary>
    ///     Counts one reader on the current mapping and pins it. A reader that raced a replacement retries on the
    ///     mapping that replaced it, so a borrow admitted after the store reads the replacement; false only when the
    ///     current mapping's release has begun.
    /// </summary>
    internal bool TryTakeBorrow([NotNullWhen(true)] out BlockMapping? mapping)
    {
        while (true)
        {
            var candidate = Current;
            var taken = candidate.ReleaseState.TryTakeBorrow();
            Hooks._borrowCounted?.Invoke();
            if (ReferenceEquals(candidate, Current))
            {
                mapping = taken ? candidate : null;
                return taken;
            }

            if (taken)
            {
                candidate.ReleaseState.ReturnBorrow();
            }
        }
    }

    /// <summary>
    ///     Publishes <paramref name="replacement" /> as the lineage's current mapping in one volatile store and
    ///     retires the mapping it replaces, which stays mapped until the lineage is released. Readers that pinned
    ///     the retired mapping finish on it. The caller holds the write gate, so no writer is on the retired mapping.
    /// </summary>
    /// <returns>The retired mapping, for its caller to schedule the deletion of a renamed-aside file on.</returns>
    /// <exception cref="ObjectDisposedException">The lineage has been handed to a release.</exception>
    internal BlockMapping Replace(BlockFile replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isSealed || _current.IsReleased, this);
            var retired = _current;
            _retired.Add(retired);
            Volatile.Write(ref _current, new BlockMapping(replacement));
            return retired;
        }
    }

    /// <summary>The bytes of every mapping this lineage retired, which stay mapped and on disk until the lineage is released.</summary>
    internal long RetiredBytes
    {
        get
        {
            lock (_gate)
            {
                return _retired.Sum(mapping => mapping.Block.Length);
            }
        }
    }

    /// <summary>
    ///     Hands every mapping of the lineage to a release: the retired ones first, then the current one. No
    ///     replacement is published afterwards, so the list is complete.
    /// </summary>
    internal RootBlockRelease[] Seal()
    {
        lock (_gate)
        {
            _isSealed = true;
            return [.. _retired.Select(mapping => mapping.ReleaseState), _current.ReleaseState];
        }
    }

    /// <summary>
    ///     Releases every mapping of the lineage without waiting, for a lineage no reader was ever admitted to.
    /// </summary>
    internal void ReleaseUnborrowed()
    {
        foreach (var release in Seal())
        {
            release.ReleaseUnborrowed();
        }
    }

    /// <summary>
    ///     Releases every mapping of the lineage, waiting for each one's borrows to drain before unmapping it.
    ///     Returns once all of them are unmapped.
    /// </summary>
    internal async ValueTask DrainAndReleaseAsync()
    {
        foreach (var release in Seal())
        {
            await release.DrainAndReleaseAsync().ConfigureAwait(false);
        }
    }
}
