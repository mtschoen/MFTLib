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
}
