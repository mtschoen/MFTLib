using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     A materialized copy of the header values of one drive's block, read through the
///     production block validation. It holds no mapping, so it stays valid after the index or
///     the block file it was read from is gone.
/// </summary>
/// <param name="ProducerKind">Which producer built the block.</param>
/// <param name="RowCount">The block's highest used slot plus one, free slots and deleted files included.</param>
/// <param name="CacheTag">The consumer cache identity stored in the header.</param>
public sealed record SyntheticDriveHeader(ProducerKind ProducerKind, uint RowCount, CacheTag CacheTag)
{
    internal static SyntheticDriveHeader From(BlockFile block)
    {
        ref readonly var header = ref block.Header;
        return new SyntheticDriveHeader(header.ProducerKind, header.RowCount, header.CacheTag);
    }
}
