namespace MFTLib.Index;

/// <summary>High-level phase values used by scan progress consumers.</summary>
public enum IndexScanPhase
{
    /// <summary>
    ///     Emitted by <see cref="EnumerationProducer" /> while walking managed filesystem directories.
    /// </summary>
    Enumerating,

    /// <summary>
    ///     Emitted by the broker-backed producer while parsing raw MFT records.
    /// </summary>
    ParsingMft,

    /// <summary>
    ///     Emitted by the broker-backed producer while transferring parsed records into
    ///     the shared block format.
    /// </summary>
    Transferring,

    /// <summary>
    ///     Emitted by <see cref="FileIndex" /> exactly once per drive scan, after the producer's
    ///     last sample for that drive, whether the scan succeeded, failed or was cancelled
    ///     (<see cref="IndexScanProgress.Outcome" /> says which). Every producer, enumeration
    ///     or MFT-backed, is covered. A rescan, and a retry after a lost catch-up, are each a
    ///     scan of their own and each report one, so track finished drives as a set.
    /// </summary>
    Finished,
}
