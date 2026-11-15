namespace MFTLib.Index;

/// <summary>
///     What one read goes through: the view's <see cref="RootBlock" />, which every handle it mints names, and the one
///     mapping of its lineage the read pinned. A query pins the mapping its borrow took and reads rows, names and
///     postings only through <see cref="Block" />, never through <see cref="RootBlock.Block" />, so a replacement that
///     lands mid-query cannot mix two mappings in one answer.
/// </summary>
/// <param name="RootBlock">The view's root block: identity, view binding and path rules.</param>
/// <param name="Block">The pinned mapping's block, which the read keeps mapped.</param>
readonly record struct PinnedRootBlock(RootBlock RootBlock, BlockFile Block)
{
    /// <summary>
    ///     Pins the current mapping without a borrow, for a caller that holds the write gate (where no replacement can
    ///     be published) or for one unborrowed handle read that reads it once.
    /// </summary>
    internal static PinnedRootBlock Current(RootBlock rootBlock) => new(rootBlock, rootBlock.Block);
}
