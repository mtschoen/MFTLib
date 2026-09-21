namespace MFTLib.Index;

/// <summary>
///     The journal facts a checkpoint decision rests on, lifted out of the platform's
///     <c>USN_JOURNAL_DATA</c> so the decision and its tests do not depend on Windows.
/// </summary>
internal readonly record struct JournalWindow(
    ulong JournalId, long FirstUsn, long NextUsn, long AllocationDelta, long MaximumSize);

/// <summary>
///     Decides whether a cached block's journal checkpoint can still be resumed, by reading
///     the live journal through the same unelevated volume-root handle the settings query
///     uses. A warm start that adopts an unresumable checkpoint does not fail at open: it
///     arms a watch that dies on its first read, so the drive ends up rescanned anyway, with
///     nothing left to tell the user why. Checking first turns that into one cold scan and
///     one <see cref="JournalCheckpointLoss" />.
/// </summary>
static class JournalCheckpointCheck
{
    /// <summary>
    ///     A test seam with no synchronization, which is safe only while the test host runs
    ///     one test at a time. Mirrors <see cref="UsnJournalSettingsQuery._queryOverride" />.
    /// </summary>
    internal static Func<char, JournalWindow?>? _journalOverride;

    /// <summary>
    ///     Returns the loss when <paramref name="checkpointUsn" /> can no longer be resumed on
    ///     <paramref name="driveLetter" />, and null when it can or when the volume cannot say.
    ///     A volume with no journal, a non-NTFS volume, or any other refusal of the query
    ///     yields null: MFTLib reports causes it can detect and invents none.
    /// </summary>
    /// <param name="driveLetter">The drive to read the journal of.</param>
    /// <param name="checkpointJournalId">The journal id the cached block was written against.</param>
    /// <param name="checkpointUsn">The USN the cached block would resume from.</param>
    public static JournalCheckpointLoss? Check(char driveLetter, ulong checkpointJournalId, long checkpointUsn)
    {
        if (ReadJournal(driveLetter) is not { } journal)
        {
            return null;
        }

        var firstUsn = journal.FirstUsn;
        var nextUsn = journal.NextUsn;
        var allocationDelta = journal.AllocationDelta;
        if (firstUsn < 0 || nextUsn < firstUsn || checkpointUsn < 0 || allocationDelta <= 0)
        {
            // Nothing coherent to report, and no reason to refuse the warm start over it.
            return null;
        }

        var maximumSize = journal.MaximumSize;

        if (journal.JournalId != checkpointJournalId)
        {
            return new JournalCheckpointLoss
            {
                DriveLetter = driveLetter,
                Cause = JournalCheckpointLossCause.JournalRecreated,
                CheckpointUsn = checkpointUsn,
                FirstUsn = firstUsn,
                NextUsn = nextUsn,
                AllocationDelta = allocationDelta,
                MaximumSize = maximumSize
            };
        }

        if (checkpointUsn >= firstUsn)
        {
            // The checkpoint is still inside the journal, so the warm start can resume it.
            return null;
        }

        return new JournalCheckpointLoss
        {
            DriveLetter = driveLetter,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = checkpointUsn,
            FirstUsn = firstUsn,
            NextUsn = nextUsn,
            AllocationDelta = allocationDelta,
            MaximumSize = maximumSize,
            BytesBehind = JournalSizeArithmetic.BytesBehind(checkpointUsn, firstUsn),
            SizeThatWouldHaveRetained =
                JournalSizeArithmetic.SizeThatWouldHaveRetained(checkpointUsn, nextUsn, allocationDelta)
        };
    }

    static JournalWindow? ReadJournal(char driveLetter)
    {
        if (_journalOverride is { } journalOverride)
        {
            return journalOverride(driveLetter);
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var handle = UsnJournalVolumeInterop.OpenVolumeRoot(
                driveLetter, "check whether its USN journal still holds the cached checkpoint");
            var data = UsnJournalVolumeInterop.QueryJournalData(handle);
            return new JournalWindow(data.UsnJournalId, data.FirstUsn, data.NextUsn,
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

    internal static IDisposable OverrideJournalForTest(Func<char, JournalWindow?> journal)
    {
        var previous = _journalOverride;
        _journalOverride = journal;
        return new RestoreJournal(previous);
    }

    sealed class RestoreJournal(Func<char, JournalWindow?>? previous) : IDisposable
    {
        public void Dispose()
        {
            _journalOverride = previous;
        }
    }
}
