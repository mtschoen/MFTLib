using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A journal record carries no size and no last-write observation, only its own event time. The mutator
///     therefore keeps what a scan observed when an event cannot have changed it, and marks it unknown when an
///     event can have: it never writes the event time into the last-write column. The seeded file is row 7, which
///     the synthetic block scanned at <see cref="ScanMoment" /> with a known size of 99 bytes.
/// </summary>
[TestClass]
public class JournalMutatorMetadataFreshnessTests
{
    const uint FileRowIndex = 7;
    const long ScannedSize = 99;
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    static void AssertScanObservationsKept(FileRow row)
    {
        Assert.IsTrue(row.SizeKnown);
        Assert.IsTrue(row.LastWriteTimeKnown);
        Assert.AreEqual(ScannedSize, row.Size);
        Assert.AreEqual(ScanMoment.Ticks, row.ModifiedTicks);
    }

    [TestMethod]
    public async Task Rename_KeepsTheScanObservationsAndTakesTheJournalAttributes()
    {
        await using var fixture = new MutatorFixture();

        var changes = fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "renamed.txt", UsnReason.RenameNewName, fixture.Timestamp,
                fileAttributes: FileAttributes.ReadOnly)
        ]);

        Assert.AreEqual(FileChangeKind.Renamed, changes.Single().Kind);
        var row = fixture.Block.Rows[(int)FileRowIndex];
        AssertScanObservationsKept(row);
        Assert.AreEqual((uint)FileAttributes.ReadOnly, row.Attributes);
        Assert.AreEqual(ScanMoment, changes.Single().Entry.LastWriteTime);
        Assert.AreEqual(fixture.Timestamp, changes.Single().Timestamp, "The event time stays on the change.");
    }

    [TestMethod]
    public async Task Move_KeepsTheScanObservations()
    {
        await using var fixture = new MutatorFixture();

        fixture.Apply([CreateEntry(FileRowIndex, 5, "notes.txt", UsnReason.RenameNewName, fixture.Timestamp)]);

        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.AreEqual(5u, row.ParentRow);
        AssertScanObservationsKept(row);
    }

    [TestMethod]
    public async Task CloseRepeatingARename_KeepsTheScanObservationsAndTakesTheCloseAttributes()
    {
        await using var fixture = new MutatorFixture();

        fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "renamed.txt", UsnReason.RenameNewName, fixture.Timestamp, sequenceNumber: 1),
            CreateEntry(FileRowIndex, 6, "renamed.txt", UsnReason.RenameNewName | UsnReason.Close,
                fixture.Timestamp.AddMinutes(1), sequenceNumber: 1, fileAttributes: FileAttributes.Hidden)
        ]);

        var row = fixture.Block.Rows[(int)FileRowIndex];
        AssertScanObservationsKept(row);
        Assert.AreEqual((uint)FileAttributes.Hidden, row.Attributes);
    }

    [TestMethod]
    [DataRow("DataOverwrite")]
    [DataRow("DataExtend")]
    [DataRow("DataTruncation")]
    public async Task ContentModification_InvalidatesTheSizeAndTheLastWriteTime(string reasonName)
    {
        var reason = Enum.Parse<UsnReason>(reasonName);
        await using var fixture = new MutatorFixture();

        var change = fixture.Apply([CreateEntry(FileRowIndex, 6, "notes.txt", reason, fixture.Timestamp)]).Single();

        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
        Assert.AreEqual(0L, row.Size);
        Assert.AreEqual(0L, row.ModifiedTicks);
        Assert.IsFalse(change.Entry.IsSizeKnown);
        Assert.IsFalse(change.Entry.IsLastWriteTimeKnown);
        Assert.AreEqual(DateTime.MinValue, change.Entry.LastWriteTime);
    }

    [TestMethod]
    public async Task BasicInfoChange_InvalidatesTheLastWriteTimeAndKeepsTheSize()
    {
        await using var fixture = new MutatorFixture();

        fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.BasicInfoChange, fixture.Timestamp,
                fileAttributes: FileAttributes.Hidden)
        ]);

        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.IsFalse(row.LastWriteTimeKnown);
        Assert.AreEqual(0L, row.ModifiedTicks);
        Assert.IsTrue(row.SizeKnown);
        Assert.AreEqual(ScannedSize, row.Size);
        Assert.AreEqual((uint)FileAttributes.Hidden, row.Attributes);
    }

    [TestMethod]
    public async Task BasicInfoChange_AfterAContentChange_KeepsTheSizeUnknown()
    {
        await using var fixture = new MutatorFixture();
        fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.DataExtend | UsnReason.Close, fixture.Timestamp,
                sequenceNumber: 1)
        ]);

        fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.BasicInfoChange, fixture.Timestamp.AddMinutes(1),
                sequenceNumber: 1)
        ]);

        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
    }

    [TestMethod]
    public async Task ContentModificationOfADirectory_InvalidatesSizeAndLastWriteTime()
    {
        await using var fixture = new MutatorFixture();

        fixture.Apply(
        [
            CreateEntry(6, 5, "documents", UsnReason.DataExtend, fixture.Timestamp,
                fileAttributes: FileAttributes.Directory)
        ]);

        var row = fixture.Block.Rows[6];
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
    }

    [TestMethod]
    public async Task CloseRepeatingAnAlreadyReportedContentChange_KeepsTheRowUnknown()
    {
        await using var fixture = new MutatorFixture();

        fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.DataExtend, fixture.Timestamp, sequenceNumber: 1),
            CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.DataExtend | UsnReason.Close,
                fixture.Timestamp.AddMinutes(1), sequenceNumber: 1)
        ]);

        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.IsFalse(row.LastWriteTimeKnown);
        Assert.AreEqual(0L, row.ModifiedTicks, "The close record time is never written into the column.");
    }

    [TestMethod]
    public async Task Create_ProducesAFileRowWithUnknownSizeAndLastWriteTime()
    {
        await using var fixture = new MutatorFixture();

        var change = fixture.Apply([CreateEntry(9, 6, "fresh.txt", UsnReason.FileCreate, fixture.Timestamp)]).Single();

        var row = fixture.Block.Rows[9];
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
        Assert.AreEqual(0L, row.Size);
        Assert.AreEqual(0L, row.ModifiedTicks);
        Assert.IsFalse(change.Entry.IsLastWriteTimeKnown);
        Assert.AreEqual(DateTime.MinValue, change.Entry.LastWriteTime);
    }

    [TestMethod]
    public async Task Create_OfADirectory_IsUnknownForSizeAndLastWriteTime()
    {
        await using var fixture = new MutatorFixture();

        fixture.Apply(
        [
            CreateEntry(9, 6, "fresh", UsnReason.FileCreate, fixture.Timestamp,
                fileAttributes: FileAttributes.Directory)
        ]);

        var row = fixture.Block.Rows[9];
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
    }

    [TestMethod]
    public async Task CreateReusingAKnownRowSlot_ReplacesTheScanObservationsWithUnknown()
    {
        await using var fixture = new MutatorFixture();
        fixture.Apply([CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.FileDelete | UsnReason.Close,
            fixture.Timestamp, sequenceNumber: 1)]);

        fixture.Apply([CreateEntry(FileRowIndex, 6, "reused.txt", UsnReason.FileCreate, fixture.Timestamp.AddMinutes(1),
            sequenceNumber: 2)]);

        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.IsFalse(row.IsDeleted);
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
        Assert.AreEqual(0L, row.ModifiedTicks);
    }

    [TestMethod]
    [DataRow("DataOverwrite")]
    [DataRow("BasicInfoChange")]
    [DataRow("RenameNewName")]
    [DataRow("SecurityChange")]
    public async Task HydratedRow_IsUnknownForSizeAndLastWriteTime(string reasonName)
    {
        var reason = Enum.Parse<UsnReason>(reasonName);
        await using var fixture = new MutatorFixture();

        fixture.Apply([CreateEntry(20, 6, "source.cs", reason, fixture.Timestamp)]);

        var row = fixture.Block.Rows[20];
        Assert.IsTrue(row.IsInUse);
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
        Assert.AreEqual(0L, row.ModifiedTicks);
    }

    [TestMethod]
    public async Task OtherReasons_KeepTheScanObservationsAndTakeTheJournalAttributes()
    {
        await using var fixture = new MutatorFixture();

        fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.SecurityChange, fixture.Timestamp,
                fileAttributes: FileAttributes.Archive | FileAttributes.ReadOnly)
        ]);

        var row = fixture.Block.Rows[(int)FileRowIndex];
        AssertScanObservationsKept(row);
        Assert.AreEqual((uint)(FileAttributes.Archive | FileAttributes.ReadOnly), row.Attributes);
    }

    [TestMethod]
    public async Task RenameCarryingAContentReason_InvalidatesTheObservationsAndRenames()
    {
        await using var fixture = new MutatorFixture();

        var change = fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "renamed.txt", UsnReason.RenameNewName | UsnReason.DataExtend,
                fixture.Timestamp)
        ]).Single();

        Assert.AreEqual(FileChangeKind.Renamed, change.Kind);
        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
    }

    [TestMethod]
    public async Task BareClose_RefreshesAttributesAndKeepsTheScanObservations()
    {
        await using var fixture = new MutatorFixture();

        var changes = fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "notes.txt", UsnReason.Close, fixture.Timestamp,
                fileAttributes: FileAttributes.ReadOnly)
        ]);

        Assert.AreEqual(0, changes.Count);
        var row = fixture.Block.Rows[(int)FileRowIndex];
        AssertScanObservationsKept(row);
        Assert.AreEqual((uint)FileAttributes.ReadOnly, row.Attributes);
    }

    [TestMethod]
    public async Task RenameWithAContentReason_WhenTheNamePoolIsFull_StillInvalidatesAndAdvancesGeneration()
    {
        await using var fixture = new MutatorFixture();
        fixture.Block.Header.NamePoolUsed = fixture.Block.Header.NamePoolCapacity;
        var generation = fixture.Block.Header.Generation;

        var changes = fixture.Apply(
        [
            CreateEntry(FileRowIndex, 6, "a-new-name-that-cannot-fit.txt", UsnReason.RenameNewName | UsnReason.DataExtend,
                fixture.Timestamp, sequenceNumber: 1)
        ]);

        Assert.AreEqual(0, changes.Count);
        Assert.IsTrue(fixture.Block.Header.IsCompactionNeeded);
        var row = fixture.Block.Rows[(int)FileRowIndex];
        Assert.IsFalse(row.SizeKnown);
        Assert.IsFalse(row.LastWriteTimeKnown);
        Assert.AreEqual(generation + 1, fixture.Block.Header.Generation, "The metadata mutation must be recorded.");
    }
}
