using MFTLib.Index;
using MFTLib.Tests.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Replays the USN records a live watch on a real volume actually delivers for a
///     create-modify-delete cycle through the mutator: the raw stream contains the
///     intermediate record and the close record for every operation, and the reported
///     change stream must contain one change per real transition (with <c>DeletePending</c>
///     followed by <c>Deleted</c> when a handle is held across the delete). Record numbers
///     are remapped onto synthetic rows so the replay needs no multi-gigabyte block;
///     batch boundaries, order, reasons, sequence numbers, and timestamps are preserved.
/// </summary>
/// <remarks>
///     The journal window is the records this test's own file operations write: from the cursor
///     queried before the cycle to the cursor queried after it. NTFS writes every record the
///     product's promise depends on before the deleting call returns: <c>FileDelete|Close</c> when
///     no other handle is open, otherwise the <c>RenameOldName</c>/<c>RenameNewName</c> move into
///     <c>$Extend\$Deleted</c>. The <c>FileDelete|Close</c> that completes a delete-pending row is
///     written when the last handle closes; when another process (a scanner) owns that handle, the
///     record lies outside the window and the test neither waits for it nor expects
///     <c>Deleted</c>, because the product promises it only at that later close.
/// </remarks>
[TestClass]
public class JournalCloseCoalescingLiveTests
{
    private const uint VolumeRootRow = 5;
    private const uint UserDirectoryRow = 6;
    private const uint ExtendDirectoryRow = 11;
    private const uint DeletedDirectoryRow = 12;
    private const uint ReplayedFileRow = 20;

    private readonly record struct FileReference(ulong RecordNumber, ushort SequenceNumber, ulong ParentRecordNumber);

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public Task LiveWatch_CreateModifyDeleteCycle_ReportsOneChangePerRealTransition()
    {
        return RunCreateModifyDeleteCycleAsync(holdShareDeleteHandleAcrossDelete: false);
    }

    // A share-delete handle held across the delete forces the POSIX delete-pending path every time:
    // NTFS moves the file into \$Extend\$Deleted (RenameOldName/RenameNewName under a different parent)
    // before the FileDelete|Close record, and the mutator reports DeletePending then Deleted with no Renamed.
    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public Task LiveWatch_DeleteWhileHandleHeld_ReportsDeletePendingThenDeleted()
    {
        return RunCreateModifyDeleteCycleAsync(holdShareDeleteHandleAcrossDelete: true);
    }

    // The record stream of the 2026-11-13 failure (issue 889): another process opened the file between
    // the modify close and the delete, so the delete moved it into \$Extend\$Deleted (parent record 29)
    // and its FileDelete|Close was not written while the test watched.
    [TestMethod]
    public Task Replay_DeleteWhileAnotherProcessHoldsTheFile_ExpectsDeletePendingWithoutDeleted()
    {
        return VerifyReplayedTransitionsAsync(SyntheticCycle(SyntheticDeletePendingMove), SyntheticFileReference,
            SyntheticFileName);
    }

    [TestMethod]
    public Task Replay_AnotherProcessReleasesTheFileInsideTheWindow_ExpectsDeletePendingThenDeleted()
    {
        return VerifyReplayedTransitionsAsync(
            SyntheticCycle([.. SyntheticDeletePendingMove, (UsnReason.FileDelete | UsnReason.Close, DeletedParentRecord)]),
            SyntheticFileReference, SyntheticFileName);
    }

    [TestMethod]
    public Task Replay_DirectDelete_ExpectsDeleted()
    {
        return VerifyReplayedTransitionsAsync(
            SyntheticCycle([(UsnReason.FileDelete | UsnReason.Close, SyntheticParentRecord)]),
            SyntheticFileReference, SyntheticFileName);
    }

    [TestMethod]
    public async Task Replay_WindowWithoutTheDelete_FailsTheVerification()
    {
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            VerifyReplayedTransitionsAsync(SyntheticCycle([]), SyntheticFileReference, SyntheticFileName));
    }

    [TestMethod]
    public async Task ReadWindow_OvershootingBatch_ExcludesDeletesAtAndAfterEnd_Issue889()
    {
        var cycle = SyntheticCycle([.. SyntheticDeletePendingMove,
            (UsnReason.FileDelete | UsnReason.Close, DeletedParentRecord),
            (UsnReason.FileDelete | UsnReason.Close, DeletedParentRecord)]);
        var entries = cycle.SelectMany(batch => batch).ToArray();
        var start = new UsnJournalCursor(1, entries[0].Usn);
        var end = new UsnJournalCursor(1, entries[7].Usn);

        async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> Watch()
        {
            await Task.CompletedTask;
            yield return (entries[..3], new UsnJournalCursor(1, entries[3].Usn));
            yield return (entries[3..], new UsnJournalCursor(1, entries[^1].Usn + 96));
            Assert.Fail("The reader must stop once the post-batch cursor reaches the window end.");
        }

        var batches = await LiveJournalWindow.ReadAsync(Watch(), start, end, TestContext.CancellationToken);

        Assert.HasCount(2, batches);
        Assert.AreSequenceEqual(entries[..3], batches[0]);
        Assert.AreSequenceEqual(entries[3..7], batches[1]);
        await VerifyReplayedTransitionsAsync(batches, SyntheticFileReference, SyntheticFileName);
    }

    private async Task RunCreateModifyDeleteCycleAsync(bool holdShareDeleteHandleAcrossDelete)
    {
        if (!ElevationUtilities.DefaultProvider.IsElevated())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var temporaryFile = new TempFile("mftlib-close-test", create: false);
        var tempPath = temporaryFile.Path;
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(tempPath));
        var volumeKey = !string.IsNullOrEmpty(volumeRoot) && volumeRoot.Length >= 2 && volumeRoot[1] == ':'
            ? new VolumeKey(volumeRoot[0])
            : new VolumeKey('C');
        using var volume = MftVolume.Open(volumeKey);
        var tempFileName = Path.GetFileName(tempPath);

        var cycleStart = volume.Journal.QueryUsnJournalCursor();
        await File.WriteAllTextAsync(tempPath, "create", CancellationToken.None);
        await File.AppendAllTextAsync(tempPath, "modify", CancellationToken.None);
        if (holdShareDeleteHandleAcrossDelete)
        {
            using var heldHandle = new FileStream(tempPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            File.Delete(tempPath);
        }
        else
        {
            File.Delete(tempPath);
        }

        var cycleEnd = volume.Journal.QueryUsnJournalCursor();
        var batches = await LiveJournalWindow.ReadAsync(volume, cycleStart, cycleEnd, TestContext.CancellationToken);

        var fileReference = batches.SelectMany(batch => batch)
            .Where(e => e.IsCreate && e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase))
            .Select(e => (FileReference?)new FileReference(e.RecordNumber, e.SequenceNumber, e.ParentRecordNumber))
            .FirstOrDefault();
        Assert.IsNotNull(fileReference, $"The journal window holds no FileCreate record for {tempFileName}.");
        if (holdShareDeleteHandleAcrossDelete)
        {
            Assert.Contains(e => IsDeletePendingRename(e, fileReference.Value), batches.SelectMany(batch => batch),
                "Expected a RenameNewName record outside the original parent (POSIX delete-pending move).");
        }

        await VerifyReplayedTransitionsAsync(batches, fileReference.Value, tempFileName);
    }

    // Expects one change per transition the window's records show: Created and Modified, DeletePending when
    // the delete moved the file into \$Extend\$Deleted, and Deleted when the window holds the FileDelete record.
    private static async Task VerifyReplayedTransitionsAsync(List<UsnJournalEntry[]> batches,
        FileReference fileReference, string fileName)
    {
        var fileEntries = batches.SelectMany(batch => batch)
            .Where(e => IsTrackedFile(e, fileReference))
            .ToArray();
        var records = DescribeRecords(fileEntries);

        // Premise guard: the fix targets the record shape NTFS writes. Without both an
        // intermediate create record and a create-close record in the raw stream, the
        // replay below would prove nothing.
        Assert.Contains(e => e.IsCreate && !e.IsClose, fileEntries,
            $"Expected an intermediate FileCreate record before the close record. Records: {records}");
        Assert.Contains(e => e.IsCreate && e.IsClose, fileEntries,
            $"Expected a FileCreate|Close record. Records: {records}");

        var movedToDeleted = fileEntries.Any(e => IsDeletePendingRename(e, fileReference));
        var deleteRecorded = fileEntries.Any(e => e.IsDelete);
        Assert.IsTrue(movedToDeleted || deleteRecorded,
            "The delete wrote neither FileDelete|Close nor a move into $Extend\\$Deleted before File.Delete returned. "
            + $"Records: {records}");
        List<FileChangeKind> expectedKinds = [FileChangeKind.Created, FileChangeKind.Modified];
        if (movedToDeleted)
        {
            expectedKinds.Add(FileChangeKind.DeletePending);
        }

        if (deleteRecorded)
        {
            expectedKinds.Add(FileChangeKind.Deleted);
        }

        await using var fixture = new MutatorFixture();
        var fixtureWriter = new BlockWriter(fixture.Block);
        fixtureWriter.TryWriteRow(ExtendDirectoryRow, "$Extend", new RowColumns(ParentRow: VolumeRootRow,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, fixture.Timestamp.Ticks, SequenceNumber: 1));
        fixtureWriter.TryWriteRow(DeletedDirectoryRow, "$Deleted", new RowColumns(ParentRow: ExtendDirectoryRow,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, fixture.Timestamp.Ticks, SequenceNumber: 1));

        var changes = new List<FileChange>();
        DateTime? lastContentClose = null;
        long ticksAfterLastContentClose = 0;
        foreach (var batch in batches)
        {
            var replayed = batch
                .Where(e => IsTrackedFile(e, fileReference))
                .Select(e => UsnJournalEntry.Create(new UsnJournalEntryOptions
                {
                    RecordNumber = ReplayedFileRow,
                    ParentRecordNumber = e.ParentRecordNumber == fileReference.ParentRecordNumber
                        ? UserDirectoryRow
                        : DeletedDirectoryRow,
                    SequenceNumber = e.SequenceNumber,
                    Usn = e.Usn,
                    TimestampUtc = e.TimestampUtc,
                    Reason = e.Reason,
                    FileAttributes = e.FileAttributes,
                    FileName = e.FileName
                }))
                .ToArray();
            if (replayed.Length == 0)
            {
                continue;
            }

            changes.AddRange(fixture.Apply(replayed));
            foreach (var entry in replayed)
            {
                if (entry.IsClose && !entry.IsDelete)
                {
                    lastContentClose = entry.TimestampUtc;
                    ticksAfterLastContentClose = fixture.Block.Rows[(int)ReplayedFileRow].ModifiedTicks;
                }
            }
        }

        // Sequence equality also proves a POSIX delete-pending move into $Extend\$Deleted never reports Renamed.
        Assert.AreSequenceEqual(expectedKinds, [.. changes.Select(change => change.Kind)],
            $"Expected one change per real transition. Records: {records}");

        var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", fileName);
        foreach (var change in changes)
        {
            Assert.AreEqual(expectedPath, change.Path);
            Assert.IsNull(change.PreviousPath);
        }

        Assert.IsTrue(fixture.Block.Rows[(int)ReplayedFileRow].IsDeleted);
        Assert.IsNotNull(lastContentClose, "Expected a close record for the create-write cycle.");
        Assert.AreEqual(0L, ticksAfterLastContentClose,
            "The close record's timestamp must never be written into the last-write column.");
        Assert.IsFalse(fixture.Block.Rows[(int)ReplayedFileRow].LastWriteTimeKnown,
            "The create-write cycle invalidated the last-write observation and no scan restored it.");
    }

    private const string SyntheticFileName = "mftlib-close-test-synthetic.tmp";
    private const ulong SyntheticParentRecord = 0x4A12;
    private const ulong DeletedParentRecord = 29;
    private static readonly FileReference SyntheticFileReference = new(0xEAADF, 7, SyntheticParentRecord);

    private static readonly (UsnReason Reason, ulong ParentRecord)[] SyntheticDeletePendingMove =
    [
        (UsnReason.RenameOldName, SyntheticParentRecord),
        (UsnReason.RenameNewName, DeletedParentRecord)
    ];

    // The create and modify cycles as NTFS writes them, one record per batch, followed by the delete records.
    private static List<UsnJournalEntry[]> SyntheticCycle(IEnumerable<(UsnReason Reason, ulong ParentRecord)> delete)
    {
        (UsnReason Reason, ulong ParentRecord)[] cycles =
        [
            (UsnReason.FileCreate, SyntheticParentRecord),
            (UsnReason.DataExtend | UsnReason.FileCreate, SyntheticParentRecord),
            (UsnReason.DataExtend | UsnReason.FileCreate | UsnReason.Close, SyntheticParentRecord),
            (UsnReason.DataExtend, SyntheticParentRecord),
            (UsnReason.DataExtend | UsnReason.Close, SyntheticParentRecord)
        ];
        var timestamp = new DateTime(2026, 11, 14, 1, 38, 1, DateTimeKind.Utc);
        return [.. cycles.Concat(delete).Select((record, index) => new[]
        {
            UsnJournalEntry.Create(new UsnJournalEntryOptions
            {
                RecordNumber = SyntheticFileReference.RecordNumber,
                ParentRecordNumber = record.ParentRecord,
                SequenceNumber = SyntheticFileReference.SequenceNumber,
                Usn = 4096 + (index * 96),
                TimestampUtc = timestamp,
                Reason = record.Reason,
                FileAttributes = FileAttributes.Archive,
                FileName = record.ParentRecord == DeletedParentRecord ? "006B000000EAADF40B307D75" : SyntheticFileName
            })
        })];
    }

    private static bool IsTrackedFile(UsnJournalEntry entry, FileReference reference)
    {
        return entry.RecordNumber == reference.RecordNumber && entry.SequenceNumber == reference.SequenceNumber;
    }

    private static bool IsDeletePendingRename(UsnJournalEntry entry, FileReference reference)
    {
        return IsTrackedFile(entry, reference) && (entry.Reason & UsnReason.RenameNewName) != 0 && !entry.IsDelete
            && entry.ParentRecordNumber != reference.ParentRecordNumber;
    }

    private static string DescribeRecords(UsnJournalEntry[] fileEntries)
    {
        var descriptions = fileEntries
            .Select(e => $"[Usn={e.Usn}, Name={e.FileName}, ParentRecordNumber={e.ParentRecordNumber}, TimestampUtc={e.TimestampUtc:O}, Reason={e.Reason}, "
                + $"IsClose={e.IsClose}, IsDelete={e.IsDelete}]")
            .ToArray();
        return descriptions.Length == 0 ? "none" : string.Join(" ", descriptions);
    }

    public TestContext TestContext { get; set; } = null!;
}
