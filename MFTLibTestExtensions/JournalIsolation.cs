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
    ///     checkpoint can still be resumed. A test that warm-starts a synthetic block over a
    ///     drive letter that happens to name a real NTFS volume would have that block rejected
    ///     as <see cref="JournalCheckpointLossCause.JournalRecreated" />, because a synthetic
    ///     journal id never matches a real one, so the test would cold-scan on one machine and
    ///     warm-start on another. Once this is active the read is skipped and the check answers
    ///     "cannot say", which is what a volume with no readable journal already answers, so
    ///     warm starts behave the same way everywhere.
    ///     <para>
    ///         It does not throw. A warm start is a legitimate thing for a test to do and has
    ///         no reason to care about the journal, so the guard makes the outcome deterministic
    ///         rather than making the call an error. <see cref="DriveStatus.CheckpointLoss" />
    ///         is therefore always null on any index a consumer test opens under this guard.
    ///         The journal override that MFTLib's own tests use to produce one is internal and
    ///         is not available to consumer test assemblies: a consumer test that needs a
    ///         checkpoint loss constructs the public <see cref="JournalCheckpointLoss" /> record
    ///         itself and feeds it to the code under test.
    ///     </para>
    ///     Activation is in-process and is not inherited by child processes.
    /// </remarks>
    public static void ForbidLiveJournalReads()
    {
        JournalCheckpointCheck.ForbidLiveJournalReads();
    }
}
