namespace MFTLib.Index;

/// <summary>
///     Builds one drive's block from the Master File Table. Declared here rather than in the
///     broker so <see cref="MFTLib.Index" /> states what it needs without referencing the
///     elevated broker that supplies it, which is the namespace boundary the library keeps.
///     A caller closes over its own broker launcher inside the delegate.
/// </summary>
public delegate Task<MftBlockProduceResult> MftBlockProducer(
    MftBlockProduceRequest request, CancellationToken cancellationToken);

/// <summary>
///     What the index needs one drive's block to be. The producer creates the file at
///     <see cref="BlockPath" /> exactly: cache mode against no-cache mode is already resolved
///     by the time this request is built, and <see cref="DeleteOnClose" /> says which one it
///     was.
/// </summary>
public sealed record MftBlockProduceRequest
{
    public required char DriveLetter { get; init; }

    public required uint VolumeSerial { get; init; }

    public required string BlockPath { get; init; }

    public bool DeleteOnClose { get; init; }

    public IProgress<IndexScanProgress>? Progress { get; init; }
}

/// <summary>
///     One finished block plus the journal cursor armed before the scan began, so a watch
///     resumes from before the scan rather than after it and nothing that changed during the
///     scan is lost. <paramref name="SkippedRecordCount" /> counts records the producer could
///     not place, which becomes the drive's warning the same way an enumeration walk's
///     access-denied subtree count does.
/// </summary>
public sealed record MftBlockProduceResult(
    BlockFile Block,
    ulong JournalId,
    long NextUsn,
    int SkippedRecordCount,
    bool CompactionNeeded);
