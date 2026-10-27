using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>The header values of a block written by <see cref="SyntheticBlock.WriteCached" />.</summary>
public sealed record SyntheticBlockOptions
{
    /// <summary>The producer recorded in the header. An enumeration block uses <see cref="RootRow" /> 0.</summary>
    public ProducerKind ProducerKind { get; init; } = ProducerKind.Mft;

    /// <summary>The row of the volume root. The rows written must include it.</summary>
    public uint RootRow { get; init; } = 5;

    /// <summary>The journal position stamped into the header, from which a watch would resume.</summary>
    public SyntheticJournalCursor JournalCursor { get; init; }

    /// <summary>Becomes <see cref="RootBlockStatus.ScanTimestamp" /> when an index adopts the block, so a test about scan age sets it explicitly.</summary>
    public DateTime ScanTimestamp { get; init; }

    /// <summary>A warm start adopts the block only when this equals the <see cref="FileIndexOptions.CacheTag" /> the index is opened with.</summary>
    public CacheTag CacheTag { get; init; }

    /// <summary>
    ///     The scan shape stamped into the header. Null stamps the shape an index opened with default row-shaping
    ///     options requires for <see cref="ProducerKind" />, so a warm start adopts the block.
    /// </summary>
    internal ulong? ScanShape { get; init; }

    /// <summary>The shape stamped into the header: <see cref="ScanShape" />, or the default for the producer kind.</summary>
    internal ulong ResolveScanShape() => ScanShape ?? SyntheticScanShape.Default(ProducerKind);
}
