namespace MFTLib.Index;

/// <summary>How one drive scan ended, carried by its <see cref="IndexScanPhase.Finished" /> sample.</summary>
public enum IndexScanOutcome
{
    /// <summary>The producer returned a finished block, which the scan then publishes or adopts.</summary>
    Succeeded,

    /// <summary>The producer failed or returned no block, so the drive gains no new block.</summary>
    Failed,

    /// <summary>The caller's token cancelled the scan before the producer finished.</summary>
    Cancelled,
}
