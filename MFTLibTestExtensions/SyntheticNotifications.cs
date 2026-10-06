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
    /// <param name="driveLetter">The uppercase ASCII drive letter from the file name.</param>
    /// <param name="volumeSerial">The volume serial from the file name.</param>
    /// <param name="path">The full path to the cached file.</param>
    /// <param name="sizeBytes">The file size in bytes.</param>
    /// <param name="lastWriteTime">The file last-write time in UTC.</param>
    /// <returns>The entry.</returns>
    public static CachedBlockFile CreateCachedBlockFile(char driveLetter, uint volumeSerial, string path,
        long sizeBytes, DateTime lastWriteTime) =>
        new(driveLetter, volumeSerial, path, sizeBytes, lastWriteTime);

    /// <summary>Builds the result of deleting one cached block.</summary>
    /// <param name="file">The inventory entry captured before deletion.</param>
    /// <param name="outcome">Whether deletion succeeded, ownership was unavailable, or deletion failed.</param>
    /// <param name="failureReason">The filesystem error message for a failure; otherwise null.</param>
    /// <returns>The result.</returns>
    public static CachedBlockDeletionResult CreateCachedBlockDeletionResult(CachedBlockFile file,
        CachedBlockDeletionOutcome outcome, string? failureReason) =>
        new(file, outcome, failureReason);

    /// <summary>Builds a directory entry rejected from the cache inventory.</summary>
    /// <param name="path">The full path to the rejected file.</param>
    /// <param name="reason">A human-readable explanation of the rejection.</param>
    /// <returns>The rejection.</returns>
    public static CachedBlockRejection CreateCachedBlockRejection(string path, string reason) =>
        new(path, reason);

    /// <summary>Builds an inspection result for one cached block.</summary>
    /// <param name="file">The inventory entry the inspection describes.</param>
    /// <param name="availability">Whether the block was available, un-lockable, or invalid.</param>
    /// <param name="validation">The validation outcome, or null for an un-lockable block.</param>
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
    /// <param name="driveLetter">The drive the fault names.</param>
    /// <param name="exception">The exception behind the fault.</param>
    /// <returns>The fault.</returns>
    public static WatchFault CreateWatchFault(WatchFaultKind kind, char driveLetter, Exception exception) =>
        new(kind, driveLetter, exception);

    /// <summary>Builds the exception a scan carries when it loses its journal catch-up.</summary>
    /// <param name="recoveryStopped">Whether the scan operation stopped retrying at this loss.</param>
    /// <param name="message">What was lost and what happens next.</param>
    /// <returns>The exception.</returns>
    public static JournalCatchUpLostException CreateJournalCatchUpLostException(bool recoveryStopped,
        string message) =>
        new(recoveryStopped, message);

    /// <summary>Builds the report that one drive settled while opening.</summary>
    /// <param name="driveLetter">The settled drive.</param>
    /// <param name="settledDriveCount">The settle order of this drive, counted from 1.</param>
    /// <param name="totalDriveCount">How many drives the open was configured with.</param>
    /// <returns>The report.</returns>
    public static IndexDriveOpened CreateIndexDriveOpened(char driveLetter, int settledDriveCount,
        int totalDriveCount) =>
        new(driveLetter, settledDriveCount, totalDriveCount);

    /// <summary>Builds one scan progress sample.</summary>
    /// <param name="driveLetter">The drive whose block is being produced.</param>
    /// <param name="phase">The current stage of the scan.</param>
    /// <param name="rowsWritten">Rows written so far.</param>
    /// <param name="totalRows">The expected total rows when known; otherwise null.</param>
    /// <param name="currentDirectory">The directory being enumerated, or null.</param>
    /// <param name="outcome">The terminal outcome on a finished sample; otherwise null.</param>
    /// <returns>The sample.</returns>
    public static IndexScanProgress CreateIndexScanProgress(char driveLetter, IndexScanPhase phase,
        uint rowsWritten, uint? totalRows = null, string? currentDirectory = null,
        IndexScanOutcome? outcome = null) =>
        new(driveLetter, phase, rowsWritten)
        {
            TotalRows = totalRows,
            CurrentDirectory = currentDirectory,
            Outcome = outcome
        };

    /// <summary>
    ///     Builds one drive status. A fixture sets <see cref="DriveStatus.WatchSupported" /> and the optional
    ///     failure and watch values with a <c>with</c> expression on the result.
    /// </summary>
    /// <param name="driveLetter">The drive.</param>
    /// <param name="state">Whether the drive can answer queries, needs a rescan, or has no block.</param>
    /// <param name="blockSource">Where the block came from.</param>
    /// <param name="liveRowCount">The rows a query can return.</param>
    /// <param name="compactionNeeded">The block header compaction flag.</param>
    /// <param name="scanTimestamp">When the current block production completed.</param>
    /// <returns>The status.</returns>
    public static DriveStatus CreateDriveStatus(char driveLetter, DriveState state, BlockSource blockSource,
        uint liveRowCount, bool compactionNeeded, DateTime scanTimestamp) =>
        new(driveLetter, state, blockSource, liveRowCount, compactionNeeded, scanTimestamp);

    /// <summary>
    ///     Builds a journal checkpoint loss report with no derived sizes and no raw journal positions. A
    ///     fixture sets <see cref="JournalCheckpointLoss.BytesBehind" /> and
    ///     <see cref="JournalCheckpointLoss.SizeThatWouldHaveRetained" /> with a <c>with</c> expression on the
    ///     result, and the positions with <see cref="SyntheticCheckpointLoss.WithJournalPositions" />.
    /// </summary>
    /// <param name="driveLetter">The drive, in upper case.</param>
    /// <param name="cause">Which situation the loss was.</param>
    /// <param name="detectedDuring">Which check found the loss.</param>
    /// <param name="allocationDelta">The journal allocation unit.</param>
    /// <param name="maximumSize">The journal configured maximum size.</param>
    /// <returns>The report.</returns>
    public static JournalCheckpointLoss CreateJournalCheckpointLoss(char driveLetter,
        JournalCheckpointLossCause cause, JournalCheckpointLossDetection detectedDuring, long allocationDelta,
        long maximumSize) =>
        new(driveLetter, detectedDuring, cause, allocationDelta, maximumSize);
}
