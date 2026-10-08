using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="ScriptedDriveWatch.FailApply" /> makes the index's own apply step fail, so the
///     fault reaches the consumer through the production classification and recovery.
/// </summary>
[TestClass]
public class ScriptedApplyFaultTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task FailApply_IsReportedAsAnApplyFaultCarryingTheScriptedException()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var failure = new InvalidOperationException("the batch cannot be applied");

        harness.Source.WatchFor('T').FailApply(failure);
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await harness.Source.WatchFor('U').Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 9000));

        Assert.AreSame(failure, fault.Exception);
        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual("the batch cannot be applied", harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn, "the other drive keeps watching");
    }

    [TestMethod]
    public async Task FailApply_AppliesEarlierBatchesAndNothingQueuedAfterIt()
    {
        using var harness = new WatchHarness('T');
        harness.Index.HoldEveryRecovery();
        var applying = harness.HoldFirstApply('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var watch = harness.Source.WatchFor('T');

        var before = watch.Queue(WatchHarness.Batch(10, "before.txt", nextUsn: 5000));
        await applying.Entered.WaitAsync(Token);
        watch.FailApply(new IOException("apply failed"));
        var after = watch.Queue(WatchHarness.Batch(11, "after.txt", nextUsn: 6000));
        applying.Release();
        await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await before;

        Assert.IsTrue(after.IsCanceled, "the batch behind the failure settles unapplied");
        Assert.AreEqual(5000L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual("before.txt", harness.Changes.Single().Entry.Name);
    }

    [TestMethod]
    public async Task FailApply_RecoversTheDriveOntoAFreshWatchThatAcceptsBatches()
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var faulted = harness.Source.WatchFor('T');

        faulted.FailApply(new IOException("apply failed"));
        await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');
        await harness.WaitForRecoveryAsync('T');
        var restarted = harness.Source.WatchFor('T');
        await restarted.Publish(WatchHarness.Batch(10, "later.txt", nextUsn: 7000));

        Assert.AreNotSame(faulted, restarted);
        Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
        Assert.AreEqual(7000L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual(1, harness.Faults.Count);
    }

    [TestMethod]
    public async Task FailApply_RejectsANullExceptionAndAClosedWatch()
    {
        using var harness = new WatchHarness('T');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        var watch = harness.Source.WatchFor('T');

        Assert.ThrowsException<ArgumentNullException>(() => watch.FailApply(null!));
        watch.FailApply(new IOException("apply failed"));
        await harness.WaitForFaultAsync(WatchFaultKind.Apply, 'T');

        Assert.ThrowsException<InvalidOperationException>(() => watch.FailApply(new IOException("again")));
    }

    [TestMethod]
    public void FailingEntryList_ThrowsOnEveryWayOfReadingIt()
    {
        var failure = new IOException("unreadable");
        var list = new FailingEntryList(failure);

        Assert.AreSame(failure, Assert.ThrowsException<IOException>(() => list.Count));
        Assert.AreSame(failure, Assert.ThrowsException<IOException>(() => list[0]));
        Assert.AreSame(failure, Assert.ThrowsException<IOException>(() => list.GetEnumerator()));
        Assert.AreSame(failure, Assert.ThrowsException<IOException>(
            () => ((System.Collections.IEnumerable)list).GetEnumerator()));
    }
}
