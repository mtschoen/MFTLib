namespace MFTLib.Index;

/// <summary>
///     What a block's name regions hold against what its rows reference: the stored names and pool bytes, and the
///     names and pool bytes a compacting copy would keep. Counted by <see cref="NameReferences.Count" />.
/// </summary>
/// <param name="StoredEntries">The block's published name entry count.</param>
/// <param name="ReferencedEntries">The stored names some row's descriptor word holds, whatever the row's flags.</param>
/// <param name="StoredPoolBytes">The block's used name-pool bytes.</param>
/// <param name="CompactedPoolBytes">The pool bytes the referenced names use when stored back to back in identifier order.</param>
readonly record struct NameUsage(uint StoredEntries, uint ReferencedEntries, uint StoredPoolBytes, uint CompactedPoolBytes)
{
    /// <summary>What a compacting copy drops; compared with the stored count against the one-eighth threshold.</summary>
    internal uint UnusedEntries => StoredEntries - ReferencedEntries;
}
