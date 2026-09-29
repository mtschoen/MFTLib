using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What a manual <c>RescanAsync</c> of a faulted drive does with the fault: the replaced
///     watch's fault goes with it, a later fault of the restarted watch is reported by its own stop,
///     and no other drive's outstanding fault is touched.
/// </summary>
[TestClass]
public class FileIndexWatchRecoveryFaultTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception =>
        FileIndexWatchRescanTests.ThrowsAsync<TException>(action);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveredDrive_StopDoesNotRethrowItsFault(bool applyFailure)
    {
        using var harness = new WatchHarness('C', 'D');
        await harness.Index.StartWatchingAsync('C', Token);
        await harness.Index.StartWatchingAsync('D', Token);
        if (applyFailure)
        {
            await harness.Source.HandleFor('C').Publish(new JournalBatch(null!, WatchHarness.JournalId, 5000));
            await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'C');
        }
        else
        {
            harness.Source.HandleFor('C').FailDrive(new IOException("old C fault"));
            await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        }

        Assert.IsNotNull(harness.DriveFor('C').WatchFailureMessage);
        harness.SetNextProducedCursor('C', 13, 9000);
        await harness.Index.RescanAsync('c', Token);
        Assert.IsNull(harness.DriveFor('C').WatchFailureMessage);
        Assert.AreEqual(2, harness.Source.StartsFor('C').Count);
        Assert.AreEqual(1, harness.Source.StartsFor('D').Count);
        await harness.Source.HandleFor('D').Publish(WatchHarness.Batch(9, "still-watching.txt", nextUsn: 9500));
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
        harness.Source.HandleFor('C').FailDrive(new IOException("old C fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.Index.RescanAsync('C', Token);
        harness.Source.HandleFor('C').LoseChannel(newFailure);
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
        harness.Source.HandleFor('C').FailDrive(new IOException("old C fault"));
        harness.Source.HandleFor('D').LoseChannel(remainingFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'D');
        await harness.Index.RescanAsync('C', Token);
        harness.Source.HandleFor('C').LoseChannel(newFailure);
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'C');

        var thrown = await ThrowsAsync<IOException>(() => harness.Index.StopWatchingAsync('D', Token));
        Assert.AreSame(remainingFailure, thrown);
        var recoveredThrown = await ThrowsAsync<IOException>(() => harness.Index.StopWatchingAsync('C', Token));
        Assert.AreSame(newFailure, recoveredThrown);
        Assert.AreEqual(1, harness.Source.StartsFor('D').Count);
        Assert.AreEqual(1, harness.Source.StartsFor('E').Count);
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
        harness.Source.HandleFor('C').FailDrive(new IOException("old C fault"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        await harness.Source.HandleFor('D').Publish(WatchHarness.Batch(9, "subscriber.txt", nextUsn: 9000));
        await harness.Index.RescanAsync('C', Token);

        var thrown = await ThrowsAsync<InvalidOperationException>(() => harness.Index.StopWatchingAsync('D', Token));
        Assert.AreSame(subscriberFailure, thrown);
        Assert.AreEqual(1, harness.Faults.Count(fault => fault.Kind == WatchFaultKind.Subscriber));
        await harness.Index.StopWatchingAsync('C', Token);
    }

    [TestMethod]
    public async Task FailedRestart_AfterAReplacedFaultedDrive_ThrowsTheRestartFailureAndReadsFaulted()
    {
        using var harness = new WatchHarness('C', 'D');
        var restartFailure = new IOException("rearm failed");
        await harness.Index.StartWatchingAsync('C', Token);
        harness.Source.HandleFor('C').FailDrive(new IOException("C never recovered"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'C');
        harness.Source.FailStart(restartFailure);

        var rescanThrown = await ThrowsAsync<IOException>(() => harness.Index.RescanAsync('C', Token));

        Assert.AreSame(restartFailure, rescanThrown);
        Assert.AreEqual("rearm failed", harness.DriveFor('C').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('C').WatchCatchUp);
    }
}
