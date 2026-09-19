using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

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
        using var harness = new WatchHarness(
            [new IndexWatchTarget('C', 11, 4242), new IndexWatchTarget('D', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        if (applyFailure)
        {
            await harness.PublishAsync(new JournalBatch('C', null!, 11, 5000));
        }
        else
        {
            await harness.PublishAsync(new DriveWatchFailure('C', new IOException("old C fault")));
        }

        Assert.IsNotNull(harness.Index.Drives.Single(drive => drive.DriveLetter == 'C').WatchFailureMessage);
        harness.SetNextProducedCursor('C', 13, 9000);
        await harness.Index.RescanAsync('c', Token);
        Assert.IsNull(harness.Index.Drives.Single(drive => drive.DriveLetter == 'C').WatchFailureMessage);
        Assert.AreEqual(1, harness.SourceInvocationCount);
        await harness.PublishAsync(new JournalBatch('D',
            [WatchHarness.Create(9, "still-watching.txt")], 22, 9500));
        Assert.AreEqual(9500L, harness.BlockFor('D').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RecoveredDrive_NewFailureIsRethrownInsteadOfOldFailure()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('C', 11, 4242), new IndexWatchTarget('D', 22, 8484)]);
        var oldFailure = new IOException("old C fault");
        var newFailure = new IOException("new C fault");
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('C', oldFailure));
        await harness.Index.RescanAsync('C', Token);
        await harness.PublishAsync(new DriveWatchFailure('C', newFailure));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(newFailure, thrown);
    }

    [TestMethod]
    public async Task RecoveringOneDrive_PreservesAnotherDrivesEarlierOutstandingFault()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('C', 11, 4242), new IndexWatchTarget('D', 22, 8484),
             new IndexWatchTarget('E', 33, 100)]);
        var remainingFailure = new IOException("D remains faulted");
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('C', new IOException("old C fault")));
        await harness.PublishAsync(new DriveWatchFailure('D', remainingFailure));
        await harness.Index.RescanAsync('C', Token);
        await harness.PublishAsync(new DriveWatchFailure('C', new IOException("new C fault")));

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(remainingFailure, thrown);
        Assert.AreEqual(1, harness.SourceInvocationCount);
    }

    [TestMethod]
    public async Task RecoveringDrive_PreservesSubscriberFaultObservedAfterItsOldFailure()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('C', 11, 4242), new IndexWatchTarget('D', 22, 8484)]);
        var subscriberFailure = new InvalidOperationException("subscriber remains broken");
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        harness.Index.Changed += _ => throw subscriberFailure;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('C', new IOException("old C fault")));
        await harness.PublishAsync(new JournalBatch('D',
            [WatchHarness.Create(9, "subscriber.txt")], 22, 9000));
        await harness.Index.RescanAsync('C', Token);

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(subscriberFailure, thrown);
        Assert.AreEqual(1, faults.Count(fault => fault.Kind == WatchFaultKind.Subscriber));
    }

    [TestMethod]
    public async Task FailedRearm_PreservesPreviouslyOutstandingDriveFault()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('C', 11, 4242), new IndexWatchTarget('D', 22, 8484)]);
        var oldFailure = new IOException("C never recovered");
        var armFailure = new IOException("rearm failed");
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('C', oldFailure));
        harness.FailNextArm(armFailure);
        var rescanThrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.RescanAsync('C', Token));
        Assert.AreSame(armFailure, rescanThrown);
        var stopThrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(oldFailure, stopThrown);
    }

    [TestMethod]
    public async Task RecoveredDrive_DoesNotSuppressSubsequentWholeStreamFailure()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('C', 11, 4242), new IndexWatchTarget('D', 22, 8484)]);
        var sourceFailure = new IOException("source ended with a failure");
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('C', new IOException("old C fault")));
        await harness.Index.RescanAsync('C', Token);
        await harness.FaultSourceAsync(sourceFailure);
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(sourceFailure, thrown);
    }
}
