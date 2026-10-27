using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     A materialized copy of the header values of one root's block, read through the
///     production block validation. It holds no mapping, so it stays valid after the index or
///     the block file it was read from is gone.
/// </summary>
/// <param name="ProducerKind">Which producer built the block.</param>
/// <param name="RowCount">The block's highest used slot plus one, free slots and deleted files included.</param>
/// <param name="CacheTag">The consumer cache identity stored in the header.</param>
/// <param name="ScanTimestamp">The scan timestamp stored in the header, which <see cref="SyntheticBlockEditor.Complete" /> can write back unchanged.</param>
sealed record SyntheticRootHeader(ProducerKind ProducerKind, uint RowCount, CacheTag CacheTag,
    DateTime ScanTimestamp)
{
    internal static SyntheticRootHeader From(BlockFile block)
    {
        ref readonly var header = ref block.Header;
        return new SyntheticRootHeader(header.ProducerKind, header.RowCount, header.CacheTag,
            header.ScanTimestampUtc);
    }
}
