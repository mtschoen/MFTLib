using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Builds the receive-only records MFTLib hands to a consumer, whose constructors the library keeps
///     internal because only the library produces them. A consumer test uses these to feed a handler or a
///     presenter the same value MFTLib would deliver. Every method forwards to the library constructor,
///     so a value built here passes the same validation as a delivered one.
/// </summary>
public static class SyntheticNotifications
{
    /// <summary>Builds a cache inventory entry.</summary>
    /// <param name="drive">The uppercase ASCII drive letter from the file name.</param>
    /// <param name="volumeSerial">The volume serial from the file name.</param>
    /// <param name="path">The full path to the cached file.</param>
    /// <param name="size">The file size in bytes.</param>
    /// <param name="lastWriteTime">The file last-write time in UTC.</param>
    /// <returns>The entry.</returns>
    public static CachedBlockFile CreateCachedBlockFile(RootKey drive, uint volumeSerial, string path,
        long size, DateTime lastWriteTime) =>
        new(drive, volumeSerial, path, size, lastWriteTime);

    /// <summary>Builds the result of deleting one cached block.</summary>
    /// <param name="file">The inventory entry captured before deletion.</param>
    /// <param name="outcome">Whether deletion succeeded, ownership was unavailable, or deletion failed.</param>
    /// <param name="failureMessage">The filesystem error message for a failure; otherwise null.</param>
    /// <returns>The result.</returns>
    public static CachedBlockDeletionResult CreateCachedBlockDeletionResult(CachedBlockFile file,
        CachedBlockDeletionOutcome outcome, string? failureMessage) =>
        new(file, outcome, failureMessage);

    /// <summary>Builds a directory entry rejected from the cache inventory.</summary>
    /// <param name="path">The full path to the rejected file.</param>
    /// <param name="message">A human-readable explanation of the rejection.</param>
    /// <returns>The rejection.</returns>
    public static CachedBlockRejection CreateCachedBlockRejection(string path, string message) =>
        new(path, message);

    /// <summary>Builds an inspection result for one cached block.</summary>
    /// <param name="file">The inventory entry the inspection describes.</param>
    /// <param name="availability">Whether the block was available, un-lockable, or invalid.</param>
    /// <param name="validation">The rejection reason of an invalid block; null for an available or un-lockable block.</param>
    /// <param name="producerKind">The validated header producer, or null.</param>
    /// <param name="rootDirectory">The materialized root directory, or null.</param>
    /// <param name="cacheTag">The validated block tag, or null when the block could not be inspected.</param>
    /// <returns>The inspection result.</returns>
    public static CachedBlockStatus CreateCachedBlockStatus(CachedBlockFile file,
        CachedBlockAvailability availability, BlockValidationResult? validation, ProducerKind? producerKind,
        string? rootDirectory, CacheTag? cacheTag = null) =>
        new(file, availability, validation, producerKind, rootDirectory) { CacheTag = cacheTag };

    /// <summary>Builds one applied journal change.</summary>
    /// <param name="kind">What happened to the entry.</param>
    /// <param name="entry">The changed entry.</param>
    /// <param name="path">The path the change happened at.</param>
    /// <param name="timestamp">The UTC timestamp of the journal record behind the change.</param>
    /// <param name="previousPath">The path a rename or move came from; otherwise null.</param>
    /// <returns>The change.</returns>
    public static FileChange CreateFileChange(FileChangeKind kind, FileEntry entry, string path,
        DateTime timestamp, string? previousPath = null) =>
        new(kind, entry, path, timestamp, previousPath);

    /// <summary>Builds a live watch fault.</summary>
    /// <param name="kind">Which boundary raised the fault.</param>
    /// <param name="drive">The drive the fault names.</param>
    /// <param name="exception">The exception behind the fault.</param>
    /// <returns>The fault.</returns>
    public static WatchFault CreateWatchFault(WatchFaultKind kind, VolumeKey drive, Exception exception) =>
        new(kind, drive, exception);

    /// <summary>Builds a watch state change for a consumer event handler.</summary>
    /// <param name="drive">The drive whose watch changed state.</param>
    /// <param name="watch">The drive's watch status after the change.</param>
    /// <param name="fault">The fault that caused the change, or null.</param>
    /// <returns>The state change.</returns>
    public static WatchStateChange CreateWatchStateChange(VolumeKey drive, DriveWatchStatus watch,
        WatchFault? fault) =>
        new(drive, watch, fault);

    /// <summary>Builds one drive's operation result for a consumer result handler.</summary>
    /// <param name="drive">The drive the result describes.</param>
    /// <param name="outcome">What the operation did for the drive.</param>
    /// <param name="failure">The per-drive exception for a failure; otherwise null.</param>
    /// <returns>The operation result.</returns>
    public static DriveOperationResult CreateDriveOperationResult(RootKey drive,
        DriveOperationOutcome outcome, Exception? failure) =>
        new(drive, outcome, failure);

    /// <summary>Builds the exception a scan carries when it loses its journal catch-up.</summary>
    /// <param name="recoveryStopped">Whether the scan operation stopped retrying at this loss.</param>
    /// <param name="message">What was lost and what happens next.</param>
    /// <returns>The exception.</returns>
    public static JournalCatchUpLostException CreateJournalCatchUpLostException(bool recoveryStopped,
        string message) =>
        new(recoveryStopped, message);

    /// <summary>Builds the report that one drive settled while opening.</summary>
    /// <param name="drive">The settled drive.</param>
    /// <param name="settledDriveCount">The settle order of this drive, counted from 1.</param>
    /// <param name="totalDriveCount">How many drives the open was configured with.</param>
    /// <returns>The report.</returns>
    public static IndexDriveOpened CreateIndexDriveOpened(RootKey drive, int settledDriveCount,
        int totalDriveCount) =>
        new(drive, settledDriveCount, totalDriveCount);

    /// <summary>
    ///     Builds the identity of one drive root. A fixture sets the readiness facts and verdict with a <c>with</c>
    ///     expression on the result.
    /// </summary>
    /// <param name="drive">The drive letter of <paramref name="rootDirectory" />.</param>
    /// <param name="rootDirectory">The drive root, for example <c>C:\</c>.</param>
    /// <param name="driveType">The operating system's classification of the drive.</param>
    /// <returns>The volume, not ready and not indexable until the fixture says otherwise.</returns>
    public static WindowsVolume CreateWindowsVolume(VolumeKey drive, string rootDirectory, DriveType driveType) =>
        new(drive, rootDirectory, driveType);

    /// <summary>Builds one scan progress sample.</summary>
    /// <param name="drive">The drive whose block is being produced.</param>
    /// <param name="phase">The current stage of the scan.</param>
    /// <param name="writtenRowCount">
    ///     Records scanned during parsing, rows transferred during intermediate transfer samples, or rows written during enumeration.
    ///     The final broker transfer sample uses the known parsing total, otherwise the block row count, raised to at least the maximum parsing count.
    ///     Finished samples carry the final block row count on success, otherwise zero.
    /// </param>
    /// <param name="expectedRowCount">
    ///     Expected total scanned records during parsing or transferred rows during intermediate transfer samples, when known; otherwise null.
    ///     The final broker transfer sample uses the known parsing total, otherwise the block row count, raised to at least the maximum parsing count.
    /// </param>
    /// <param name="currentDirectory">The directory being enumerated, or null.</param>
    /// <param name="outcome">The terminal outcome on a finished sample; otherwise null.</param>
    /// <returns>The sample.</returns>
    public static IndexScanProgress CreateIndexScanProgress(RootKey drive, IndexScanPhase phase,
        uint writtenRowCount, uint? expectedRowCount = null, string? currentDirectory = null,
        IndexScanOutcome? outcome = null) =>
        new(drive, phase, writtenRowCount)
        {
            ExpectedRowCount = expectedRowCount,
            CurrentDirectory = currentDirectory,
            Outcome = outcome
        };

    /// <summary>
    ///     Builds one drive status from its state, block and failure. A fixture sets the watch values with a
    ///     <c>with</c> expression on the result, nesting one for <see cref="DriveStatus.Watch" /> (for example
    ///     <c>status with { Watch = status.Watch with { IsSupported = true } }</c>).
    /// </summary>
    /// <param name="drive">The drive.</param>
    /// <param name="state">Whether the drive can answer queries, needs a rescan, or has no block.</param>
    /// <param name="block">The block behind a <see cref="DriveState.Ready" /> drive; null otherwise.</param>
    /// <param name="failure">Why the drive failed; null for a drive that did not.</param>
    /// <returns>The status.</returns>
    public static DriveStatus CreateDriveStatus(RootKey drive, DriveState state, DriveBlockStatus? block,
        DriveFailure? failure) =>
        new(drive, state)
        {
            Block = block,
            Failure = failure
        };

    /// <summary>
    ///     Builds the block part of a drive status. A fixture sets the optional cache-location and skipped-record
    ///     values with a <c>with</c> expression on the result.
    /// </summary>
    /// <param name="blockOrigin">Where the block came from.</param>
    /// <param name="liveRowCount">The rows a query can return.</param>
    /// <param name="capacityExhausted">The block header capacity-exhausted flag.</param>
    /// <param name="scanTimestamp">When the current block production completed.</param>
    /// <returns>The block status.</returns>
    public static DriveBlockStatus CreateDriveBlockStatus(BlockOrigin blockOrigin, uint liveRowCount,
        bool capacityExhausted, DateTime scanTimestamp) =>
        new()
        {
            Origin = blockOrigin,
            LiveRowCount = liveRowCount,
            IsCapacityExhausted = capacityExhausted,
            ScanTimestamp = scanTimestamp
        };

    /// <summary>Builds a drive failure.</summary>
    /// <param name="kind">Which failure it is.</param>
    /// <param name="message">The detail a consumer shows.</param>
    /// <returns>The failure.</returns>
    public static DriveFailure CreateDriveFailure(DriveFailureKind kind, string message) => new(kind, message);

    /// <summary>
    ///     Builds a journal checkpoint loss report with no derived sizes and no raw journal positions. A
    ///     fixture sets <see cref="JournalCheckpointLoss.BytesBehind" /> and
    ///     <see cref="JournalCheckpointLoss.SizeThatWouldHaveRetained" /> with a <c>with</c> expression on the
    ///     result, and the positions with <see cref="SyntheticCheckpointLoss.WithJournalPositions" />.
    /// </summary>
    /// <param name="drive">The drive, in upper case.</param>
    /// <param name="cause">Which situation the loss was.</param>
    /// <param name="detectedDuring">Which check found the loss.</param>
    /// <param name="journalSettings">The journal sizing when the loss was detected.</param>
    /// <returns>The report.</returns>
    public static JournalCheckpointLoss CreateJournalCheckpointLoss(VolumeKey drive,
        JournalCheckpointLossCause cause, JournalCheckpointLossDetection detectedDuring,
        UsnJournalSettings journalSettings) =>
        new(drive, detectedDuring, cause, journalSettings);
}
