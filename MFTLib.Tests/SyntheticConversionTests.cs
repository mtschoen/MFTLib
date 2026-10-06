using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The test package describes journal and scan data with its own public values and converts them to the
///     internal production values just before production code runs. These tests pin that conversion, which is
///     data translation only.
/// </summary>
[TestClass]
public class SyntheticConversionTests
{
    [TestMethod]
    public void SyntheticJournalRecord_CarriesEveryFieldIntoTheEntry()
    {
        var timestamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var entry = new SyntheticJournalRecord
        {
            RecordNumber = 42,
            ParentRecordNumber = 17,
            UpdateSequenceNumber = 900,
            FileName = "x.txt",
            Reason = SyntheticJournalReason.FileCreate | SyntheticJournalReason.Close,
            FileAttributes = FileAttributes.Hidden,
            Timestamp = timestamp,
            SequenceNumber = 3
        }.ToProduction();

        Assert.AreEqual(42UL, entry.RecordNumber);
        Assert.AreEqual(17UL, entry.ParentRecordNumber);
        Assert.AreEqual(900L, entry.Usn);
        Assert.AreEqual("x.txt", entry.FileName);
        Assert.AreEqual(UsnReason.FileCreate | UsnReason.Close, entry.Reason);
        Assert.AreEqual(FileAttributes.Hidden, entry.FileAttributes);
        Assert.AreEqual(timestamp, entry.TimestampUtc);
        Assert.AreEqual((ushort)3, entry.SequenceNumber);
    }

    [TestMethod]
    public void SyntheticJournalRecord_DefaultsToCloseNormalAndTheUnixEpoch()
    {
        var entry = new SyntheticJournalRecord
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            UpdateSequenceNumber = 2,
            FileName = "d.txt"
        }.ToProduction();

        Assert.AreEqual(UsnReason.Close, entry.Reason);
        Assert.AreEqual(FileAttributes.Normal, entry.FileAttributes);
        Assert.AreEqual(DateTime.UnixEpoch, entry.TimestampUtc);
        Assert.AreEqual((ushort)0, entry.SequenceNumber);
    }

    [TestMethod]
    public void SyntheticJournalReason_HasTheSameBitsAsTheProductionReasons()
    {
        var synthetic = Enum.GetValues<SyntheticJournalReason>().Select(reason => (uint)reason).Order().ToArray();
        var production = Enum.GetValues<UsnReason>().Select(reason => (uint)reason).Order().ToArray();

        CollectionAssert.AreEqual(production, synthetic);
    }

    [TestMethod]
    public void JournalRecordList_ConvertsEveryRecordInOrder()
    {
        var entries = new[]
        {
            JournalEntries.CreateSynthetic(20, 100, "first.txt"),
            JournalEntries.CreateSynthetic(21, 101, "second.txt")
        }.ToProduction();

        CollectionAssert.AreEqual(new[] { "first.txt", "second.txt" }, entries.Select(entry => entry.FileName).ToArray());
        CollectionAssert.AreEqual(new[] { 100L, 101L }, entries.Select(entry => entry.Usn).ToArray());
    }

    [TestMethod]
    public void SyntheticJournalCursor_RoundTripsThroughTheProductionCursor()
    {
        var cursor = new SyntheticJournalCursor(9, 250);

        var production = cursor.ToProduction();

        Assert.AreEqual(new UsnJournalCursor(9, 250), production);
        Assert.AreEqual(cursor, production.ToSynthetic());
    }

    [TestMethod]
    public void SyntheticScanRecord_CarriesEveryColumnIntoTheRecord()
    {
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var record = new SyntheticScanRecord
        {
            RecordNumber = 31,
            ParentRecordNumber = 30,
            FileName = "a.txt",
            IsDirectory = true,
            InUse = false,
            IsSizeKnown = false,
            FileAttributes = FileAttributes.Hidden,
            Size = 4096,
            LastWriteTime = modified,
            SequenceNumber = 2
        }.ToProduction();

        Assert.AreEqual(31UL, record.RecordNumber);
        Assert.AreEqual(30UL, record.ParentRecordNumber);
        Assert.AreEqual("a.txt", record.FileName);
        Assert.IsNull(record.FullPath);
        Assert.IsTrue(record.IsDirectory);
        Assert.IsFalse(record.InUse);
        Assert.IsFalse(record.SizeKnown);
        Assert.AreEqual(FileAttributes.Hidden, record.FileAttributes);
        Assert.AreEqual(4096L, record.Size);
        Assert.AreEqual(modified, record.ModifiedUtc);
        Assert.AreEqual((ushort)2, record.SequenceNumber);
    }

    [TestMethod]
    public void SyntheticScanRecord_DefaultsToAnInUseKnownSizeNormalFile()
    {
        var file = new SyntheticScanRecord { RecordNumber = 31, ParentRecordNumber = 30, FileName = "a.txt" }.ToProduction();
        var directory = new SyntheticScanRecord
        {
            RecordNumber = 30,
            ParentRecordNumber = 5,
            FileName = "docs",
            IsDirectory = true
        }.ToProduction();

        Assert.IsTrue(file.InUse);
        Assert.IsTrue(file.SizeKnown);
        Assert.IsFalse(file.IsDirectory);
        Assert.AreEqual(FileAttributes.Normal, file.FileAttributes);
        Assert.AreEqual(DateTime.UnixEpoch, file.ModifiedUtc);
        Assert.IsTrue(directory.IsDirectory);
        Assert.AreEqual(FileAttributes.Directory, directory.FileAttributes);
    }

    [TestMethod]
    public void Conversions_RejectNull()
    {
        Assert.ThrowsException<ArgumentNullException>(() => ((SyntheticScanRecord)null!).ToProduction());
        Assert.ThrowsException<ArgumentNullException>(() => ((SyntheticJournalRecord)null!).ToProduction());
        Assert.ThrowsException<ArgumentNullException>(() => ((IReadOnlyList<SyntheticJournalRecord>)null!).ToProduction());
    }

    [TestMethod]
    public async Task CreateSession_LaunchesTheStartedHandlesClientAndTheHandleReportsItsEnd()
    {
        var handle = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
        {
            QueryJournalCursor = _ => new SyntheticJournalCursor(7, 1000),
            GrowUsnJournal = (_, maximumSize, allocationDelta) => new UsnJournalSettings
            {
                MaximumSize = maximumSize,
                AllocationDelta = allocationDelta
            }
        });
        var launches = 0;
        var session = BrokerTestHarness.CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(handle);
        });
        try
        {
            var settings = await session.GrowUsnJournalAsync('C', 0x08000000, 0x01000000, CancellationToken.None)
                .WaitAsync(ScriptedWatchSource.HangGuard);

            Assert.AreEqual(1, launches);
            Assert.AreEqual(0x08000000L, settings.MaximumSize);
            Assert.IsFalse(handle.Ended.IsCompleted);
        }
        finally
        {
            await session.DisposeAsync();
            await handle.DisposeAsync();
        }

        await handle.Ended.WaitAsync(ScriptedWatchSource.HangGuard);
    }

    [TestMethod]
    public void CreateSession_NullLaunch_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => BrokerTestHarness.CreateSession(null!));
    }
}
