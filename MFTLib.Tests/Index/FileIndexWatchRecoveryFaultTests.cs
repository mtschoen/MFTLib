using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveredDrive_StopDoesNotReportItsPreviousFault(bool applyFailure)
    {
        using var harness = new WatchHarness('C', 'D');
        await harness.IndexFor('C').StartWatchingAsync(Token);
        await harness.IndexFor('D').StartWatchingAsync(Token);
        harness.SetNextProducedCursor('C', 13, 9000);
        if (applyFailure)
        {
            await harness.Source.WatchFor('C').Publish(new JournalBatch(null!, WatchHarness.JournalIdentifier, 5000));
            await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'C');
        }
        else
        {
            harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
            await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        }

        await harness.WaitForRecoveryAsync('C');
        Assert.IsNull(harness.DriveFor('C').Watch.FailureMessage);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('C'), 13, 9000), harness.Source.TargetsFor('C')[^1]);
        Assert.HasCount(2, harness.Source.TargetsFor('C'));
        Assert.HasCount(1, harness.Source.TargetsFor('D'));
        await harness.Source.WatchFor('D').Publish(WatchHarness.Batch(9, "still-watching.txt", nextUsn: 9500));
        Assert.AreEqual(9500L, harness.BlockFor('D').Header.UsnNextUsn);
        await harness.IndexFor('C').StopWatchingAsync(Token);
        await harness.IndexFor('D').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RecoveredDrive_StopReportsNewFailureInsteadOfOldFailure()
    {
        using var harness = new WatchHarness('C', 'D');
        var newFailure = new IOException("new C fault");
        await harness.IndexFor('C').StartWatchingAsync(Token);
        await harness.IndexFor('D').StartWatchingAsync(Token);
        harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.WaitForRecoveryAsync('C');
        harness.Source.WatchFor('C').LoseChannel(newFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'C');

        var failure = await DriveResultAssertions.FailureAsync<IOException>(() => harness.IndexFor('C').StopWatchingAsync(Token));
        Assert.AreSame(newFailure, failure);
    }

    [TestMethod]
    public async Task RecoveringOneDrive_PreservesAnotherDrivesEarlierOutstandingFault()
    {
        using var harness = new WatchHarness('C', 'D', 'E');
        var remainingFailure = new IOException("D remains faulted");
        var newFailure = new IOException("new C fault");
        await harness.IndexFor('C').StartWatchingAsync(Token);
        await harness.IndexFor('D').StartWatchingAsync(Token);
        await harness.IndexFor('E').StartWatchingAsync(Token);
        harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
        harness.Source.WatchFor('D').LoseChannel(remainingFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'D');
        await harness.WaitForRecoveryAsync('C');
        harness.Source.WatchFor('C').LoseChannel(newFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'C');

        var failure = await DriveResultAssertions.FailureAsync<IOException>(() => harness.IndexFor('D').StopWatchingAsync(Token));
        Assert.AreSame(remainingFailure, failure);
        var recoveredFailure = await DriveResultAssertions.FailureAsync<IOException>(() => harness.IndexFor('C').StopWatchingAsync(Token));
        Assert.AreSame(newFailure, recoveredFailure);
        Assert.HasCount(1, harness.Source.TargetsFor('D'));
        Assert.HasCount(1, harness.Source.TargetsFor('E'));
        await harness.IndexFor('E').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RecoveringOneDrive_PreservesAnotherDrivesSubscriberFault()
    {
        using var harness = new WatchHarness('C', 'D');
        var subscriberFailure = new InvalidOperationException("subscriber remains broken");
        harness.IndexFor('C').Changed += _ => throw subscriberFailure;
        harness.IndexFor('D').Changed += _ => throw subscriberFailure;
        await harness.IndexFor('C').StartWatchingAsync(Token);
        await harness.IndexFor('D').StartWatchingAsync(Token);
        harness.Source.WatchFor('C').FailDrive(new IOException("old C fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.Source.WatchFor('D').Publish(WatchHarness.Batch(9, "subscriber.txt", nextUsn: 9000));
        await harness.WaitForRecoveryAsync('C');

        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => harness.IndexFor('D').StopWatchingAsync(Token));
        Assert.AreSame(subscriberFailure, failure);
        Assert.ContainsSingle(fault => fault.Kind == WatchFaultKind.Subscriber, harness.Faults);
        await harness.IndexFor('C').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task FailedRestart_OfARecoveredDrive_RaisesRecoveryAndReadsFaulted()
    {
        using var harness = new WatchHarness('C', 'D');
        var restartFailure = new IOException("rearm failed");
        await harness.IndexFor('C').StartWatchingAsync(Token);
        harness.Source.FailNextStart(restartFailure);
        harness.Source.WatchFor('C').FailDrive(new IOException("C never recovered"));

        var recoveryFault = await harness.WaitForFaultAsync(WatchFaultKind.Recovery, 'C');
        await harness.WaitForRecoveryAsync('C');

        Assert.AreSame(restartFailure, recoveryFault.Exception);
        Assert.AreEqual("rearm failed", harness.DriveFor('C').Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('C').Watch.State);
    }
}
