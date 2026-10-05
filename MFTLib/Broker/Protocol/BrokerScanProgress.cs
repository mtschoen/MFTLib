namespace MFTLib;

/// <summary>
///     Progress sample emitted during an elevated broker drive scan.
/// </summary>
internal readonly record struct BrokerScanProgress
{
    /// <summary>The drive this sample describes, as the drive letter the scan was requested with.</summary>
    public required string DriveLetter { get; init; }

    /// <summary>Whether the broker is still parsing records off the volume or already transferring them into the shared block.</summary>
    public BrokerScanPhase Phase { get; init; }

    /// <summary>
    ///     Records handled so far in the current <see cref="Phase" />. The broker tracks the two
    ///     phases separately, so the value never moves backward within a phase but can drop when
    ///     the phase changes.
    /// </summary>
    public long RecordsProcessed { get; init; }

    /// <summary>
    ///     Name pool bytes written so far, not the block's total byte length. A consumer
    ///     rendering a byte figure is seeing one region of the block, not all of it.
    /// </summary>
    public long BytesProcessed { get; init; }

    /// <summary>
    ///     Records expected in the current <see cref="Phase" />, or null while that is unknown.
    ///     During parsing it is the whole-volume record total; intermediate transferring
    ///     samples report the live-record total the block writer knows, or null while it does
    ///     not. The final completion sample in the transferring phase is never null: it
    ///     reports the parsing total when one was observed, otherwise the block's written row
    ///     count, and both final counts are raised to the scan's maximum records-processed
    ///     count when that exceeds the selected total, so the final sample never reports a
    ///     total below the number of records already handled.
    /// </summary>
    public long? TotalRecords { get; init; }

    /// <summary>
    ///     Name pool bytes expected in total, on the same measure as
    ///     <see cref="BytesProcessed" />. The block scan reports the two as equal on its
    ///     final sample, so it conveys completion rather than a ratio.
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>Time since the broker began this scan, spanning both phases.</summary>
    public TimeSpan Elapsed { get; init; }
}
