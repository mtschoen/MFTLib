using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     What the automatic recovery of a faulted drive (spec 2.6.5) does with the fault: the replaced
///     watch's fault goes with it, a later fault of the restarted watch is reported by its own stop,
///     and no other drive's outstanding fault is touched.
/// </summary>
[TestClass]
public class FileIndexWatchRecoveryFaultTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveredDrive_StopDoesNotRethrowItsFault(bool applyFailure)
    {
        using var harness = new WatchHarness('C', 'D');
        await harness.Index.StartWatchingAsync('C', Token);
        await harness.Index.StartWatchingAsync('D', Token);
        harness.SetNextProducedCursor('C', 13, 9000);
        if (applyFailure)
        {
            await harness.Source.WatchFor('C').Publish(new JournalBatch(null!, WatchHarness.JournalId, 5000));
            await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'C');
        }
        else
        {
            harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
            await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        }

        await harness.WaitForRecoveryAsync('C');
        Assert.IsNull(harness.DriveFor('C').WatchFailureMessage);
        Assert.AreEqual(new IndexWatchTarget('C', 13, 9000), harness.Source.TargetsFor('C')[^1]);
        Assert.AreEqual(2, harness.Source.TargetsFor('C').Count);
        Assert.AreEqual(1, harness.Source.TargetsFor('D').Count);
        await harness.Source.WatchFor('D').Publish(WatchHarness.Batch(9, "still-watching.txt", nextUsn: 9500));
        Assert.AreEqual(9500L, harness.BlockFor('D').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('C', Token);
        await harness.Index.StopWatchingAsync('D', Token);
    }

    [TestMethod]
    public async Task RecoveredDrive_NewFailureIsRethrownInsteadOfOldFailure()
    {
        using var harness = new WatchHarness('C', 'D');
        var newFailure = new IOException("new C fault");
        await harness.Index.StartWatchingAsync('C', Token);
        await harness.Index.StartWatchingAsync('D', Token);
        harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.WaitForRecoveryAsync('C');
        harness.Source.WatchFor('C').LoseChannel(newFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'C');

        var thrown = await ThrowsAsync<IOException>(() => harness.Index.StopWatchingAsync('C', Token));
        Assert.AreSame(newFailure, thrown);
    }

    [TestMethod]
    public async Task RecoveringOneDrive_PreservesAnotherDrivesEarlierOutstandingFault()
    {
        using var harness = new WatchHarness('C', 'D', 'E');
        var remainingFailure = new IOException("D remains faulted");
        var newFailure = new IOException("new C fault");
        await harness.Index.StartWatchingAsync('C', Token);
        await harness.Index.StartWatchingAsync('D', Token);
        await harness.Index.StartWatchingAsync('E', Token);
        harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
        harness.Source.WatchFor('D').LoseChannel(remainingFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'D');
        await harness.WaitForRecoveryAsync('C');
        harness.Source.WatchFor('C').LoseChannel(newFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'C');

        var thrown = await ThrowsAsync<IOException>(() => harness.Index.StopWatchingAsync('D', Token));
        Assert.AreSame(remainingFailure, thrown);
        var recoveredThrown = await ThrowsAsync<IOException>(() => harness.Index.StopWatchingAsync('C', Token));
        Assert.AreSame(newFailure, recoveredThrown);
        Assert.AreEqual(1, harness.Source.TargetsFor('D').Count);
        Assert.AreEqual(1, harness.Source.TargetsFor('E').Count);
        await harness.Index.StopWatchingAsync('E', Token);
    }

    [TestMethod]
    public async Task RecoveringOneDrive_PreservesAnotherDrivesSubscriberFault()
    {
        using var harness = new WatchHarness('C', 'D');
        var subscriberFailure = new InvalidOperationException("subscriber remains broken");
        harness.Index.Changed += _ => throw subscriberFailure;
        await harness.Index.StartWatchingAsync('C', Token);
        await harness.Index.StartWatchingAsync('D', Token);
        harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.Source.WatchFor('D').Publish(WatchHarness.Batch(9, "subscriber.txt", nextUsn: 9000));
        await harness.WaitForRecoveryAsync('C');

        var thrown = await ThrowsAsync<InvalidOperationException>(() => harness.Index.StopWatchingAsync('D', Token));
        Assert.AreSame(subscriberFailure, thrown);
        Assert.AreEqual(1, harness.Faults.Count(fault => fault.Kind == WatchFaultKind.Subscriber));
        await harness.Index.StopWatchingAsync('C', Token);
    }

    [TestMethod]
    public async Task FailedRestart_OfARecoveredDrive_RaisesRecoveryAndReadsFaulted()
    {
        using var harness = new WatchHarness('C', 'D');
        var restartFailure = new IOException("rearm failed");
        await harness.Index.StartWatchingAsync('C', Token);
        harness.Source.FailNextStart(restartFailure);
        harness.Source.WatchFor('C').FailDrive(new IOException("C never recovered"));

        var recoveryFault = await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'C');
        await harness.WaitForRecoveryAsync('C');

        Assert.AreSame(restartFailure, recoveryFault.Exception);
        Assert.AreEqual("rearm failed", harness.DriveFor('C').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('C').WatchCatchUpState);
    }
}
