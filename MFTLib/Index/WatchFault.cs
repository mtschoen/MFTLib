namespace MFTLib.Index;

/// <summary>Identifies which boundary raised a live watch fault.</summary>
public enum WatchFaultKind
{
    /// <summary>
    ///     A <see cref="FileIndex.Changed" /> subscriber threw while receiving a batch. The drive
    ///     keeps watching; this is announced once per watch of the drive.
    /// </summary>
    Subscriber,

    /// <summary>
    ///     The drive's own watch failed (its read threw <see cref="DriveWatchFaultException" />).
    ///     The drive's watch has ended and its recovery has started: the drive reads
    ///     <see cref="WatchCatchUpState.Recovering" /> while the index rescans it and then starts
    ///     its watch again from the new block's cursor.
    /// </summary>
    Drive,

    /// <summary>
    ///     The index could not apply a batch. The block is unchanged, the drive's cursor was not
    ///     advanced, the drive's watch has ended, and its recovery has started, as for
    ///     <see cref="Drive" />.
    /// </summary>
    Apply,

    /// <summary>
    ///     A scan of the drive lost its journal catch-up: the journal proved that the cursor armed
    ///     before the scan could no longer be read when the scan finished. The exception is a
    ///     <see cref="JournalCatchUpLostException" />. The scan's block is published but cannot be
    ///     watched from its cursor. Raised by the scan operation, which rescans the drive at once
    ///     unless <see cref="JournalCatchUpLostException.RecoveryStopped" /> says it has reached
    ///     <see cref="FileIndex.LostCatchUpRecoveryLimit" />.
    /// </summary>
    CatchUpLost,

    /// <summary>
    ///     The channel carrying the drive's watch was lost, or the watch ended without being
    ///     stopped. The drive's watch has ended and no recovery starts.
    /// </summary>
    Channel,

    /// <summary>
    ///     The recovery of a faulted drive did not restore its watch: its scan or its restart
    ///     failed, or the restarted watch failed before it first caught up (the exception is then
    ///     that watch's own failure). The drive stays <see cref="WatchCatchUpState.Faulted" />, with
    ///     no further automatic recovery, until <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> or
    ///     <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> is called for it.
    /// </summary>
    Recovery
}

/// <summary>
///     A fault observed by one drive's watch pump, or by a scan of the drive that lost its
///     catch-up. Every fault names its drive.
/// </summary>
public sealed record WatchFault(WatchFaultKind Kind, char DriveLetter, Exception Exception);
