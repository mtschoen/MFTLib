using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class JournalWindowOverrideTests
{
    static SyntheticJournalWindow Trimmed => new(7, 1_500, 5_000, 64, 8_192);

    static JournalCheckpointLoss? Check(char drive = 'T') =>
        JournalCheckpointCheck.Check(drive, 7, 1_000,
            JournalCheckpointLossDetection.DriveOpening);

    [TestMethod]
    public void Override_MapsEveryFieldAndPassesDriveThrough()
    {
        char? observed = null;
        using var scope = JournalIsolation.OverrideJournalWindow(drive =>
        {
            observed = drive.Letter;
            return drive == 'T' ? Trimmed : null;
        });
        var loss = Check();
        Assert.AreEqual('T', observed);
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(1_500L, loss.FirstUsn);
        Assert.AreEqual(5_000L, loss.NextUsn);
        Assert.AreEqual(64L, loss.JournalSettings.AllocationDelta);
        Assert.AreEqual(8_192L, loss.JournalSettings.MaximumSize);
        Assert.IsNull(Check('U'));
    }

    [TestMethod]
    public void NullCallback_DoesNotClaimOwnership()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => JournalIsolation.OverrideJournalWindow(null!));
        using var scope = JournalIsolation.OverrideJournalWindow(_ => Trimmed);
        Assert.IsNotNull(Check());
    }

    [TestMethod]
    public async Task Overlap_IsRejectedAcrossAwaitAndFromAnotherThread()
    {
        using var scope = JournalIsolation.OverrideJournalWindow(_ => Trimmed);
        var failure = Assert.ThrowsExactly<InvalidOperationException>(
            () => JournalIsolation.OverrideJournalWindow(_ => null));
        Assert.Contains("already active", failure.Message);
        await Task.Run(() => Assert.ThrowsExactly<InvalidOperationException>(
            () => JournalIsolation.OverrideJournalWindow(_ => null)));
        Assert.IsNotNull(Check());
    }

    [TestMethod]
    public void StaleScopeDisposal_CannotClobberNewOwner()
    {
        var first = JournalIsolation.OverrideJournalWindow(_ => Trimmed);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, Check()!.Cause);
        first.Dispose();
        Assert.IsNull(Check());

        using var second = JournalIsolation.OverrideJournalWindow(
            _ => new SyntheticJournalWindow(8, 0, 5_000, 64, 8_192));
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, Check()!.Cause);

        first.Dispose();

        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, Check()!.Cause);
    }

    [TestMethod]
    public void ExceptionalExit_RestoresGuardAndReleasesOwnership()
    {
        JournalIsolation.ForbidLiveJournalReads();
        var failure = new IOException("synthetic observation failure");
        var actual = Assert.ThrowsExactly<IOException>(() =>
        {
            using var scope = JournalIsolation.OverrideJournalWindow(_ => throw failure);
            Check();
        });
        Assert.AreSame(failure, actual);
        Assert.IsNull(Check());
        using var next = JournalIsolation.OverrideJournalWindow(_ => Trimmed);
        Assert.IsNotNull(Check());
    }
}
