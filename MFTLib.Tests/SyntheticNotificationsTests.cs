using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     <see cref="SyntheticNotifications" /> forwards to the internal constructors of the receive-only
///     records, so each factory must hand back exactly the value the library constructor builds and
///     refuse what that constructor refuses.
/// </summary>
[TestClass]
public class SyntheticNotificationsTests
{
    static readonly DateTime Moment = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    static CachedBlockFile File() =>
        SyntheticNotifications.CreateCachedBlockFile('T', 7, @"C:\cache\T-7.mlix", 4096, Moment);

    [TestMethod]
    public void CreateCachedBlockFile_ForwardsEveryValue()
    {
        var file = File();

        Assert.AreEqual(new CachedBlockFile('T', 7, @"C:\cache\T-7.mlix", 4096, Moment), file);
        Assert.AreEqual('T', file.Drive);
        Assert.AreEqual(7u, file.VolumeSerial);
        Assert.AreEqual(@"C:\cache\T-7.mlix", file.Path);
        Assert.AreEqual(4096L, file.Size);
        Assert.AreEqual(Moment, file.LastWriteTime);
    }

    [TestMethod]
    public void CreateCachedBlockFile_RefusesANullPath()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateCachedBlockFile('T', 7, null!, 0, Moment));
    }

    [TestMethod]
    public void CreateCachedBlockDeletionResult_ForwardsEveryValue()
    {
        var file = File();

        var result = SyntheticNotifications.CreateCachedBlockDeletionResult(file,
            CachedBlockDeletionOutcome.Failed, "access denied");

        Assert.AreEqual(new CachedBlockDeletionResult(file, CachedBlockDeletionOutcome.Failed, "access denied"),
            result);
        Assert.AreEqual(file, result.File);
        Assert.AreEqual(CachedBlockDeletionOutcome.Failed, result.Outcome);
        Assert.AreEqual("access denied", result.FailureReason);
    }

    [TestMethod]
    public void CreateCachedBlockDeletionResult_RefusesANullFile()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateCachedBlockDeletionResult(null!, CachedBlockDeletionOutcome.Deleted, null));
    }

    [TestMethod]
    public void CreateCachedBlockRejection_ForwardsEveryValue()
    {
        var rejection = SyntheticNotifications.CreateCachedBlockRejection(@"C:\cache\x.mlix", "not canonical");

        Assert.AreEqual(new CachedBlockRejection(@"C:\cache\x.mlix", "not canonical"), rejection);
        Assert.AreEqual(@"C:\cache\x.mlix", rejection.Path);
        Assert.AreEqual("not canonical", rejection.Reason);
    }

    [TestMethod]
    public void CreateCachedBlockRejection_RefusesNullText()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateCachedBlockRejection(null!, "reason"));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateCachedBlockRejection("path", null!));
    }

    [TestMethod]
    public void CreateCachedBlockStatus_ForwardsEveryValueIncludingTheTag()
    {
        var file = File();
        var tag = new CacheTag("GITW", 3);

        var status = SyntheticNotifications.CreateCachedBlockStatus(file, CachedBlockAvailability.Invalid,
            BlockValidationResult.WrongMagic, ProducerKind.Mft, @"T:\", tag);

        Assert.AreEqual(
            new CachedBlockStatus(file, CachedBlockAvailability.Invalid, BlockValidationResult.WrongMagic,
                ProducerKind.Mft, @"T:\")
            { CacheTag = tag },
            status);
        Assert.AreEqual(file, status.File);
        Assert.AreEqual(CachedBlockAvailability.Invalid, status.Availability);
        Assert.AreEqual(BlockValidationResult.WrongMagic, status.Validation);
        Assert.AreEqual(ProducerKind.Mft, status.ProducerKind);
        Assert.AreEqual(@"T:\", status.RootDirectory);
        Assert.AreEqual(tag, status.CacheTag);
    }

    [TestMethod]
    public void CreateCachedBlockStatus_WithoutATag_LeavesTheTagNull()
    {
        var status = SyntheticNotifications.CreateCachedBlockStatus(File(), CachedBlockAvailability.InUse,
            null, null, null);

        Assert.IsNull(status.CacheTag);
        Assert.IsNull(status.Validation);
        Assert.IsNull(status.ProducerKind);
        Assert.IsNull(status.RootDirectory);
    }

    [TestMethod]
    public void CreateCachedBlockStatus_RefusesANullFile()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateCachedBlockStatus(null!, CachedBlockAvailability.InUse, null, null, null));
    }

    [TestMethod]
    public void CreateFileChange_ForwardsEveryValue()
    {
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Renamed, default, @"T:\new.txt",
            Moment, @"T:\old.txt");

        Assert.AreEqual(new FileChange(FileChangeKind.Renamed, default, @"T:\new.txt", Moment, @"T:\old.txt"),
            change);
        Assert.AreEqual(FileChangeKind.Renamed, change.Kind);
        Assert.AreEqual(@"T:\new.txt", change.Path);
        Assert.AreEqual(Moment, change.Timestamp);
        Assert.AreEqual(@"T:\old.txt", change.PreviousPath);
    }

    [TestMethod]
    public void CreateFileChange_WithoutAPreviousPath_LeavesItNull()
    {
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Created, default, @"T:\a.txt", Moment);

        Assert.IsNull(change.PreviousPath);
    }

    [TestMethod]
    public void CreateFileChange_RefusesANullPath()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateFileChange(FileChangeKind.Created, default, null!, Moment));
    }

    [TestMethod]
    public void CreateWatchFault_ForwardsEveryValue()
    {
        var exception = new IOException("pipe closed");

        var fault = SyntheticNotifications.CreateWatchFault(WatchFaultKind.Channel, 'T', exception);

        Assert.AreEqual(new WatchFault(WatchFaultKind.Channel, 'T', exception), fault);
        Assert.AreEqual(WatchFaultKind.Channel, fault.Kind);
        Assert.AreEqual('T', fault.Drive);
        Assert.AreSame(exception, fault.Exception);
    }

    [TestMethod]
    public void CreateWatchFault_RefusesANullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateWatchFault(WatchFaultKind.Drive, 'T', null!));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreateWatchStateChange_ForwardsEveryValue(bool includeFault)
    {
        var watch = SyntheticNotifications.CreateDriveStatus('T', DriveState.Offline, null, null).Watch
            with
        { State = WatchState.Faulted, StateVersion = 7, FailureMessage = "watch failed" };
        WatchFault? fault = includeFault
            ? SyntheticNotifications.CreateWatchFault(WatchFaultKind.Channel, 'T', new IOException("pipe closed"))
            : null;

        var change = SyntheticNotifications.CreateWatchStateChange('t', watch, fault);

        Assert.AreEqual(new DriveKey('T'), change.Drive);
        Assert.AreSame(watch, change.Watch);
        Assert.AreSame(fault, change.Fault);
        Assert.AreEqual(new WatchStateChange('T', watch, fault), change);
    }

    [TestMethod]
    [DataRow(DriveOperationOutcome.Succeeded)]
    [DataRow(DriveOperationOutcome.NotApplicable)]
    [DataRow(DriveOperationOutcome.Failed)]
    public void CreateDriveOperationResult_ForwardsEveryValue(DriveOperationOutcome outcome)
    {
        Exception? failure = outcome == DriveOperationOutcome.Failed
            ? new IOException("scan failed")
            : null;

        var result = SyntheticNotifications.CreateDriveOperationResult('t', outcome, failure);

        Assert.AreEqual(new DriveKey('T'), result.Drive);
        Assert.AreEqual(outcome, result.Outcome);
        Assert.AreSame(failure, result.Failure);
        Assert.AreEqual(new DriveOperationResult('T', outcome, failure), result);
    }

    [TestMethod]
    public void CreateJournalCatchUpLostException_ForwardsBothValues()
    {
        var exception = SyntheticNotifications.CreateJournalCatchUpLostException(true, "scan catch-up lost");

        Assert.IsTrue(exception.IsRecoveryStopped);
        Assert.AreEqual("scan catch-up lost", exception.Message);
        Assert.IsFalse(SyntheticNotifications.CreateJournalCatchUpLostException(false, "again").IsRecoveryStopped);
    }

    [TestMethod]
    public void CreateJournalCatchUpLostException_RefusesANullMessage()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyntheticNotifications.CreateJournalCatchUpLostException(false, null!));
    }

    [TestMethod]
    public void CreateIndexDriveOpened_ForwardsEveryValue()
    {
        var opened = SyntheticNotifications.CreateIndexDriveOpened('T', 2, 5);

        Assert.AreEqual(new IndexDriveOpened('T', 2, 5), opened);
        Assert.AreEqual('T', opened.Drive);
        Assert.AreEqual(2, opened.SettledDriveCount);
        Assert.AreEqual(5, opened.TotalDriveCount);
    }

    [TestMethod]
    public void CreateIndexScanProgress_ForwardsEveryValue()
    {
        var progress = SyntheticNotifications.CreateIndexScanProgress('T', IndexScanPhase.Finished, 9, 10,
            @"T:\dir", IndexScanOutcome.Succeeded);

        Assert.AreEqual('T', progress.Drive);
        Assert.AreEqual(IndexScanPhase.Finished, progress.Phase);
        Assert.AreEqual(9u, progress.WrittenRowCount);
        Assert.AreEqual(10u, progress.ExpectedRowCount);
        Assert.AreEqual(@"T:\dir", progress.CurrentDirectory);
        Assert.AreEqual(IndexScanOutcome.Succeeded, progress.Outcome);
    }

    [TestMethod]
    public void CreateIndexScanProgress_WithoutOptionalValues_LeavesThemNull()
    {
        var progress = SyntheticNotifications.CreateIndexScanProgress('T', IndexScanPhase.ParsingMft, 1);

        Assert.AreEqual(new IndexScanProgress('T', IndexScanPhase.ParsingMft, 1), progress);
        Assert.IsNull(progress.ExpectedRowCount);
        Assert.IsNull(progress.CurrentDirectory);
        Assert.IsNull(progress.Outcome);
    }

    [TestMethod]
    public void CreateDriveStatus_ForwardsEveryValueAndLeavesTheOptionalOnesDefault()
    {
        var status = SyntheticNotifications.CreateDriveStatus('T', DriveState.Ready,
            SyntheticNotifications.CreateDriveBlockStatus(BlockOrigin.WarmStartedFromCache, 40, true, Moment), null);
        status = status with { Watch = status.Watch with { IsSupported = true } };

        Assert.AreEqual(
            new DriveStatus('T', DriveState.Ready)
            {
                Block = new DriveBlockStatus
                {
                    Origin = BlockOrigin.WarmStartedFromCache,
                    LiveRowCount = 40,
                    IsCapacityExhausted = true,
                    ScanTimestamp = Moment
                },
                Watch = new DriveWatchStatus { IsSupported = true }
            },
            status);
        Assert.AreEqual('T', status.Drive);
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, status.Block!.Origin);
        Assert.AreEqual(40u, status.Block.LiveRowCount);
        Assert.IsTrue(status.Block.IsCapacityExhausted);
        Assert.AreEqual(Moment, status.Block.ScanTimestamp);
        Assert.IsTrue(status.Watch.IsSupported);
        Assert.IsNull(status.Failure);
        Assert.IsNull(status.Watch.FailureMessage);
        Assert.IsNull(status.Watch.CheckpointLoss);
    }

    [TestMethod]
    public void CreateDriveStatus_AcceptsAWithExpressionForTheOptionalValues()
    {
        var status = SyntheticNotifications.CreateDriveStatus('T', DriveState.Ready,
            SyntheticNotifications.CreateDriveBlockStatus(BlockOrigin.ProducedByScan, 0, false, DateTime.MinValue),
            null);
        status = status with { Watch = status.Watch with { FailureMessage = "watch failed" } };

        Assert.AreEqual("watch failed", status.Watch.FailureMessage);
    }

    [TestMethod]
    public void CreateDriveStatus_CarriesAFailureAndNoBlockForAFailedDrive()
    {
        var failure = SyntheticNotifications.CreateDriveFailure(DriveFailureKind.CacheDeclined, "no usable cache");

        var status = SyntheticNotifications.CreateDriveStatus('T', DriveState.Failed, null, failure);

        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.IsNull(status.Block);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, status.Failure?.Kind);
        Assert.AreEqual("no usable cache", status.Failure?.Message);
    }

    [TestMethod]
    public void CreateJournalCheckpointLoss_ForwardsEveryValue()
    {
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('T',
            JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.LiveWatch,
            new UsnJournalSettings { AllocationDelta = 4096, MaximumSize = 32768 })
            with
        {
            BytesBehind = 4000,
            SizeThatWouldHaveRetained = 12288
        };

        Assert.AreEqual(
            new JournalCheckpointLoss('T', JournalCheckpointLossDetection.LiveWatch,
                JournalCheckpointLossCause.CheckpointTrimmed,
                new UsnJournalSettings { AllocationDelta = 4096, MaximumSize = 32768 })
            {
                BytesBehind = 4000,
                SizeThatWouldHaveRetained = 12288
            },
            loss);
        Assert.AreEqual('T', loss.Drive);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, loss.DetectedDuring);
        Assert.AreEqual(4096L, loss.JournalSettings.AllocationDelta);
        Assert.AreEqual(32768L, loss.JournalSettings.MaximumSize);
        Assert.AreEqual(4000L, loss.BytesBehind);
        Assert.AreEqual(12288L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public void CreateJournalCheckpointLoss_WithoutSizes_LeavesThemNull()
    {
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('T',
            JournalCheckpointLossCause.JournalRecreated, JournalCheckpointLossDetection.DriveOpening,
            new UsnJournalSettings { AllocationDelta = 4096, MaximumSize = 32768 });

        Assert.IsNull(loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }
}
