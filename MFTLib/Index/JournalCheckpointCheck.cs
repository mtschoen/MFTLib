using MFTLib.NativeInterop;

namespace MFTLib.Index;

/// <summary>
///     The journal facts a checkpoint decision rests on, lifted out of the platform's
///     <c>USN_JOURNAL_DATA</c> so the decision and its tests do not depend on Windows.
/// </summary>
internal readonly record struct JournalWindow(
    ulong JournalIdentifier, long FirstUsn, long NextUsn, long AllocationDelta, long MaximumSize);

/// <summary>
///     Decides whether a block's journal position can still be resumed, by reading the live
///     journal through the same unelevated volume-root handle the settings query uses. A warm
///     start that adopts an unresumable checkpoint does not fail at open: it arms a watch that
///     dies on its first read, so the drive ends up rescanned anyway, with nothing left to tell
///     the user why. Checking first turns that into one cold scan and one
///     <see cref="JournalCheckpointLoss" />. The same check answers for a watch that faults
///     mid-session, where the position asked about is the one that watch had reached, so both
///     paths report the same fields from the same arithmetic.
///     For a source with no watch source it also answers whether the journal has moved since
///     the block was stamped (<c>CheckUnmoved</c>: the live next USN must equal the stamped one exactly).
/// </summary>
static class JournalCheckpointCheck
{
    internal static readonly JournalHooks Hooks = new();

    internal sealed class JournalHooks
    {
        int _liveReadsForbidden;

        internal Func<DriveKey, JournalWindow?>? ObservationOverride
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        internal bool LiveReadsForbidden => Volatile.Read(ref _liveReadsForbidden) != 0;

        internal void DisableLiveReads() => Interlocked.Exchange(ref _liveReadsForbidden, 1);
    }

    /// <summary>
    ///     Returns the loss when <paramref name="checkpointUsn" /> can no longer be resumed on
    ///     <paramref name="drive" />, and null when it can or when the volume cannot say.
    ///     A volume with no journal, a non-NTFS volume, or any other refusal of the query
    ///     yields null: MFTLib reports causes it can detect and invents none.
    /// </summary>
    /// <param name="drive">The drive to read the journal of.</param>
    /// <param name="checkpointJournalIdentifier">The journal identifier the block's cursor was written against.</param>
    /// <param name="checkpointUsn">The USN the drive would resume from.</param>
    /// <param name="detectedDuring">
    ///     Which caller is asking, stamped onto any loss returned. The two callers run at
    ///     different moments and their reports outlive those moments, so a report has to carry
    ///     which one produced it rather than leaving a reader to infer it from context that is
    ///     gone by the time the report is read.
    /// </param>
    public static JournalCheckpointLoss? Check(DriveKey drive, ulong checkpointJournalIdentifier, long checkpointUsn,
        JournalCheckpointLossDetection detectedDuring)
        => CheckObservation(drive, checkpointJournalIdentifier, checkpointUsn, detectedDuring, requireUnmoved: false);

    /// <summary>
    ///     Checks a scan-only cache against one journal observation: recreation and trimming take
    ///     precedence, then any unequal next USN requires a rescan. An unavailable or incoherent
    ///     observation reports nothing, just as the resumability check does.
    /// </summary>
    internal static JournalCheckpointLoss? CheckUnmoved(DriveKey drive, ulong checkpointJournalIdentifier,
        long checkpointUsn, JournalCheckpointLossDetection detectedDuring)
        => CheckObservation(drive, checkpointJournalIdentifier, checkpointUsn, detectedDuring, requireUnmoved: true);

    static JournalCheckpointLoss? CheckObservation(DriveKey drive, ulong checkpointJournalIdentifier, long checkpointUsn,
        JournalCheckpointLossDetection detectedDuring, bool requireUnmoved)
    {
        if (ProcessJournal.Read(drive) is not (var journalIdentifier, var firstUsn, var nextUsn, var allocationDelta, var maximumSize))
        {
            return null;
        }

        if (firstUsn < 0 || nextUsn < firstUsn || checkpointUsn < 0 || allocationDelta <= 0)
        {
            // Nothing coherent to report, and no reason to refuse the warm start over it.
            return null;
        }

        var journalSettings = new UsnJournalSettings { AllocationDelta = allocationDelta, MaximumSize = maximumSize };

        if (journalIdentifier != checkpointJournalIdentifier)
        {
            return new JournalCheckpointLoss(drive, detectedDuring, JournalCheckpointLossCause.JournalRecreated,
                journalSettings)
            {
                CheckpointUsn = checkpointUsn,
                FirstUsn = firstUsn,
                NextUsn = nextUsn
            };
        }

        if (checkpointUsn >= firstUsn)
        {
            if (requireUnmoved && nextUsn != checkpointUsn)
            {
                return new JournalCheckpointLoss(drive, detectedDuring,
                    JournalCheckpointLossCause.JournalAdvanced, journalSettings)
                {
                    CheckpointUsn = checkpointUsn,
                    NextUsn = nextUsn
                };
            }

            // The checkpoint is still inside the journal, so the warm start can resume it.
            return null;
        }

        return new JournalCheckpointLoss(drive, detectedDuring, JournalCheckpointLossCause.CheckpointTrimmed,
            journalSettings)
        {
            CheckpointUsn = checkpointUsn,
            FirstUsn = firstUsn,
            NextUsn = nextUsn,
            BytesBehind = JournalSizeArithmetic.BytesBehind(checkpointUsn, firstUsn),
            SizeThatWouldHaveRetained = RetentionSizeHint(checkpointUsn, nextUsn, allocationDelta)
        };
    }

    static long? RetentionSizeHint(long checkpointUsn, long nextUsn, long allocationDelta)
    {
        try
        {
            return JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn, nextUsn, allocationDelta);
        }
        catch (OverflowException)
        {
            // The loss remains valid when its optional retention size cannot fit in a long.
            return null;
        }
    }

    /// <summary>
    ///     The reader <see cref="Check" /> uses: its guard is this process's one-way flag, and
    ///     nothing can replace it or its guard.
    /// </summary>
    static readonly JournalReader ProcessJournal = new(
        () => Hooks.LiveReadsForbidden, ReadLiveJournal);

    /// <summary>
    ///     Reads the journal a check decides on, from a guard and a live read. The process reads
    ///     through its own instance; a test builds another instance to exercise the unguarded
    ///     path without touching the process's guard.
    /// </summary>
    internal sealed class JournalReader(Func<bool> liveReadsForbidden, Func<DriveKey, JournalWindow?> readLive)
    {
        /// <summary>
        ///     The override when one is installed, otherwise nothing while live reads are
        ///     forbidden, otherwise the live journal.
        /// </summary>
        public JournalWindow? Read(DriveKey drive)
        {
            if (Hooks.ObservationOverride is { } journalOverride)
            {
                return journalOverride(drive);
            }

            if (liveReadsForbidden())
            {
                // A test process reads no real volume unless a test asked for one. Answering null
                // is the same answer a volume that cannot be queried already gives, so a warm
                // start behaves identically on every machine instead of depending on whether the
                // test's drive letter happens to name a real NTFS volume.
                return null;
            }

            return readLive(drive);
        }
    }

    /// <summary>
    ///     The real unelevated read, reachable from a test that deliberately wants a real
    ///     volume even while <see cref="ForbidLiveJournalReads" /> is active.
    /// </summary>
    internal static JournalWindow? ReadLiveJournal(DriveKey drive) =>
        ReadLiveJournal(drive, OperatingSystem.IsWindows());

    /// <summary>
    ///     The read behind <see cref="ReadLiveJournal(DriveKey)" />, given whether the host is Windows,
    ///     so the answer off Windows is tested on every platform.
    /// </summary>
    internal static JournalWindow? ReadLiveJournal(DriveKey drive, bool isWindows)
    {
        if (!isWindows || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var handle = UsnJournalVolumeInterop.OpenVolumeRoot(
                drive, "check whether its USN journal still holds the cached checkpoint");
            var data = UsnJournalVolumeInterop.QueryJournalData(handle);
            return new JournalWindow(data.UsnJournalIdentifier, data.FirstUsn, data.NextUsn,
                checked((long)data.AllocationDelta), checked((long)data.MaximumSize));
        }
        catch (IOException)
        {
            // The volume root would not open: not a reason to reject an otherwise valid block.
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No active journal (ERROR_JOURNAL_NOT_ACTIVE) or another refusal of the query.
            return null;
        }
    }

    /// <summary>
    ///     Stops this process's automatic checkpoint observations reading any real volume's journal for the rest of its life, so a
    ///     warm start in a test decides the same way whatever drives the host happens
    ///     to have. One way and idempotent, like the cache-directory guard; a test that wants
    ///     a real volume installs an override over <see cref="ReadLiveJournal(DriveKey)" />.
    /// </summary>
    internal static void ForbidLiveJournalReads()
    {
        Hooks.DisableLiveReads();
    }
}
