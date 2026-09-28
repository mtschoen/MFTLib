using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Opt-in protection against tests reading a real volume's USN journal.</summary>
public static class JournalIsolation
{
    /// <summary>
    ///     Stops this test process reading any real volume's USN change journal for the
    ///     remainder of its life. Call from the test assembly's module initializer, before
    ///     opening any indexes. Repeated calls are safe; there is no reset.
    /// </summary>
    /// <remarks>
    ///     Opening a drive normally reads the live journal to decide whether a cached block's
    ///     checkpoint can still be resumed, and a drive whose live watch faults reads it again
    ///     to decide whether the position that watch reached is still in the journal. A test
    ///     that warm-starts or watches a synthetic block over a drive letter that happens to
    ///     name a real NTFS volume would have that block rejected as
    ///     <see cref="JournalCheckpointLossCause.JournalRecreated" />, because a synthetic
    ///     journal id never matches a real one, so the test would cold-scan on one machine and
    ///     warm-start on another. Once this is active the read is skipped and the check answers
    ///     "cannot say", which is what a volume with no readable journal already answers, so
    ///     warm starts and watch faults behave the same way everywhere.
    ///     <para>
    ///         It does not throw. Warm-starting and watching are legitimate things for a test
    ///         to do and have no reason to care about the journal, so the guard makes the
    ///         outcome deterministic rather than making the call an error.
    ///         Without a synthetic override, a new index opened under this guard reports no
    ///         <see cref="DriveStatus.CheckpointLoss" /> from journal observations.
    ///         Consumer tests can use <see cref="OverrideJournalWindow" /> to supply retained,
    ///         trimmed, or recreated windows and exercise real open-time and watch-fault
    ///         transitions. The scope restores the previous behavior on disposal; the guard
    ///         remains enabled. Existing reports on an already-open index are not cleared
    ///         by disposing a scope.
    ///     </para>
    ///     Activation is in-process and is not inherited by child processes.
    /// </remarks>
    public static void ForbidLiveJournalReads()
    {
        JournalCheckpointCheck.ForbidLiveJournalReads();
    }

    /// <summary>Supplies synthetic journal observations until the returned scope is disposed.</summary>
    /// <param name="journal">
    ///     Called for each open-time or watch-fault checkpoint query with its drive letter.
    ///     Return null when the journal cannot say whether the checkpoint is retained.
    /// </param>
    /// <returns>A process-global scope that restores the previous observation behavior.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="InvalidOperationException">Another public override scope is active.</exception>
    /// <remarks>
    ///     This does not change the one-way live-read isolation flag. Synthetic observations
    ///     take precedence over that flag, and null never falls back to a live read.
    ///     Use a nonparallel fixture for the entire scope lifetime, including awaited work.
    ///     Finish index operations and stop/dispose watches before disposing the scope.
    ///     Callbacks may run on background threads; synchronize mutable observation state.
    ///     Nested and overlapping scopes are rejected. Repeated disposal is harmless.
    ///     Callback exceptions propagate through the calling index operation's existing policy.
    ///     Disposal does not drain callbacks already in progress.
    /// </remarks>
    public static IDisposable OverrideJournalWindow(Func<char, SyntheticJournalWindow?> journal)
    {
        return JournalWindowOverride.Create(journal);
    }
}
