namespace MFTLib.Index;

/// <summary>
///     Which of MFTLib's two journal-position checks found the loss. A report outlives the
///     moment that produced it, so without this a consumer reading
///     <see cref="DriveWatchStatus.CheckpointLoss" /> from inside a
///     <see cref="FileIndex.WatchFaulted" /> handler could not tell a report about the fault it
///     is handling from one the open left behind.
/// </summary>
public enum JournalCheckpointLossDetection
{
    /// <summary>
    ///     Found while opening the drive, checking a cached block's checkpoint before adopting
    ///     it. The rescan this explains has already happened, so there is nothing to do about
    ///     the drive itself: the report is the standing explanation of why this session
    ///     cold-scanned, and of the journal size that would have avoided it.
    /// </summary>
    DriveOpening,

    /// <summary>
    ///     Found when the drive's live watch faulted, against the position that watch had
    ///     reached. This one is actionable: nothing after that position has been applied, so
    ///     the drive stays behind until <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> rebuilds it. A
    ///     watch fault that leaves the report reading <see cref="DriveOpening" />, or leaves it
    ///     null, was not the journal outrunning the index.
    /// </summary>
    LiveWatch,

    /// <summary>
    ///     Proven by the broker host against the live journal when a scan's catch-up failed, for the
    ///     cursor armed before that scan: the journal trimmed past it, or was recreated, while the
    ///     drive was being scanned. The scan's block is complete but cannot be resumed from that
    ///     cursor.
    /// </summary>
    ScanCatchUp
}

/// <summary>Why a cached block needs a rescan rather than adoption or journal catch-up.</summary>
public enum JournalCheckpointLossCause
{
    /// <summary>
    ///     The journal is the same one the checkpoint came from, and it has trimmed past it.
    ///     <see cref="JournalCheckpointLoss.SizeThatWouldHaveRetained" /> says the size a
    ///     journal would need to be at least to have kept the checkpoint, or null when that
    ///     size does not fit in a <see cref="long" />.
    /// </summary>
    CheckpointTrimmed,

    /// <summary>
    ///     The journal was deleted and recreated, so it carries a different journal id and a
    ///     fresh USN space. The checkpoint names a record in a journal that no longer exists;
    ///     no journal size would have preserved it, and none is suggested.
    /// </summary>
    JournalRecreated,

    /// <summary>
    ///     The journal still holds the cursor but its next USN differs. A scan-only source
    ///     cannot catch up, so it must rescan; no retention size would avoid that rescan.
    /// </summary>
    JournalAdvanced
}

/// <summary>
///     What MFTLib found when a drive's journal position could no longer be adopted or resumed, so the
///     drive needs a full scan rather than a catch-up: the position that was lost, the journal
///     as it stood when the loss was detected, and the size a journal would need to be at least
///     to have kept it. The position is a cached block's checkpoint when a warm start found it
///     gone or moved, and the position a live watch had reached when that watch faulted.
///     <para>
///         Every number here is read off the volume, not inferred. USNs are byte offsets into
///         the journal, so the distance between two of them is a byte count.
///     </para>
/// </summary>
public sealed record JournalCheckpointLoss
{
    /// <summary>Creates a report with its identifying values and no journal positions.</summary>
    /// <param name="driveLetter">The drive, in upper case.</param>
    /// <param name="detectedDuring">Which check found the loss.</param>
    /// <param name="cause">Which situation the loss was.</param>
    /// <param name="journalSettings">The journal's sizing when the loss was detected.</param>
    internal JournalCheckpointLoss(char driveLetter, JournalCheckpointLossDetection detectedDuring,
        JournalCheckpointLossCause cause, UsnJournalSettings journalSettings)
    {
        DriveLetter = driveLetter;
        DetectedDuring = detectedDuring;
        Cause = cause;
        JournalSettings = journalSettings;
    }

    /// <summary>Upper case, matching the letter this drive was configured with.</summary>
    public char DriveLetter { get; init; }

    /// <summary>
    ///     Which check found this, and so whether the drive still needs anything done about it.
    ///     A report is replaced only by a newer report for the same drive or cleared by a
    ///     successful <see cref="FileIndex.RescanAsync(char, CancellationToken)" />, never by an unrelated watch fault,
    ///     so a handler that acts on a loss reads this before deciding the fault it is handling
    ///     was the journal's doing.
    /// </summary>
    public JournalCheckpointLossDetection DetectedDuring { get; init; }

    /// <summary>
    ///     Which situation this was, and therefore whether a journal size is
    ///     offered at all. MFTLib reports only causes it can detect from the journal itself.
    /// </summary>
    public JournalCheckpointLossCause Cause { get; init; }

    /// <summary>
    ///     Where the drive's block left off, and so the point the journal would have had to
    ///     still reach back to for this drive to be caught up instead of rescanned. That is the
    ///     cached checkpoint for a loss found at open, and the position the live watch had
    ///     reached for one found when a watch faulted. Internal: consumers read the sizes derived
    ///     from it, <see cref="BytesBehind" /> and <see cref="SizeThatWouldHaveRetained" />.
    /// </summary>
    internal long CheckpointUsn { get; init; }

    /// <summary>The oldest USN the journal still retained when the loss was detected.</summary>
    internal long FirstUsn { get; init; }

    /// <summary>The USN the journal's next record would be written at.</summary>
    internal long NextUsn { get; init; }

    /// <summary>The journal's sizing when the loss was detected: its configured maximum size and the allocation unit that sizes are rounded up to.</summary>
    public UsnJournalSettings JournalSettings { get; init; }

    /// <summary>
    ///     How far behind the journal the checkpoint had fallen, in bytes:
    ///     the oldest USN the journal retained minus the checkpoint. Null for
    ///     <see cref="JournalCheckpointLossCause.JournalRecreated" />, where USN spaces differ,
    ///     and <see cref="JournalCheckpointLossCause.JournalAdvanced" />, where no trimming occurred.
    /// </summary>
    public long? BytesBehind { get; init; }

    /// <summary>
    ///     A journal's maximum size in bytes would need to be at least this large to have kept
    ///     the checkpoint readable: the journal's next USN minus the checkpoint,
    ///     rounded up to <see cref="UsnJournalSettings.AllocationDelta" />, plus one more allocation delta. The
    ///     margin follows NTFS's documented trimming behavior in CREATE_USN_JOURNAL_DATA and
    ///     USN_JOURNAL_DATA, not a live measurement. This is the size to offer the user
    ///     alongside the broker's journal grow request (<c>BrokerFrameKind.GrowUsnJournal</c>), when there is one to offer.
    ///     Null for <see cref="JournalCheckpointLossCause.JournalRecreated" /> and
    ///     <see cref="JournalCheckpointLossCause.JournalAdvanced" />, where no size would have helped,
    ///     and also null for <see cref="JournalCheckpointLossCause.CheckpointTrimmed" />
    ///     when the size does not fit in a <see cref="long" />: a consumer branches on
    ///     <see cref="Cause" /> to tell the two apart, not on whether this is null.
    /// </summary>
    public long? SizeThatWouldHaveRetained { get; init; }

    /// <summary>
    ///     Projects the recorded retention size and allocation delta into a journal growth target.
    ///     No sizing is recomputed, no volume is queried and no journal is changed.
    /// </summary>
    /// <param name="target">
    ///     The recorded retention size as the maximum size, with the recorded allocation delta;
    ///     default when no eligible target exists.
    /// </param>
    /// <returns>
    ///     True only for a trimmed checkpoint with a positive representable retention size greater
    ///     than the recorded maximum, and a positive recorded allocation delta.
    /// </returns>
    /// <remarks>
    ///     Query fresh journal settings and compare the target maximum with the current maximum
    ///     immediately before requesting growth. This report describes sizing at detection time.
    /// </remarks>
    public bool TryGetGrowthTarget(out UsnJournalSettings target)
    {
        target = default;
        if (Cause != JournalCheckpointLossCause.CheckpointTrimmed ||
            SizeThatWouldHaveRetained is not { } maximumSize ||
            maximumSize <= JournalSettings.MaximumSize ||
            maximumSize <= 0 ||
            JournalSettings.AllocationDelta <= 0)
        {
            return false;
        }

        target = new UsnJournalSettings
        {
            MaximumSize = maximumSize,
            AllocationDelta = JournalSettings.AllocationDelta
        };
        return true;
    }
}
