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
[TestClass]
public class JournalCloseCoalescingLiveTests
{
    // Cancellation bound only, never an asserted duration. When another process (a first-run scanner)
    // holds a handle on the new file, Windows POSIX delete semantics rename the file into \$Extend\$Deleted
    // and the FileDelete|Close record arrives later under that new name, so the file is tracked by its
    // file reference, not its name. Healthy runs exit on the record.
    private const int DeleteCloseWaitSeconds = 60;
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
        var cursor = volume.Journal.QueryUsnJournalCursor();
        var tempFileName = Path.GetFileName(tempPath);

        var batches = new List<UsnJournalEntry[]>();
        var sawDeleteClose = false;
        FileReference? fileReference = null;
        var watchTask = Task.Run(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(DeleteCloseWaitSeconds));
            // ReSharper disable once AccessToDisposedClosure
            // volume is captured from outer scope; watchTask is awaited before volume's using exits.
            await foreach (var (batch, _) in volume.Journal.WatchUsnJournal(cursor, timeout.Token))
            {
                batches.Add(batch);
                fileReference ??= batch
                    .Where(e => e.IsCreate && e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase))
                    .Select(e => (FileReference?)new FileReference(e.RecordNumber, e.SequenceNumber,
                        e.ParentRecordNumber))
                    .FirstOrDefault();
                if (batch.Any(e => IsTrackedFile(e, fileReference) && e.IsDelete && e.IsClose))
                {
                    sawDeleteClose = true;
                    break;
                }
            }
        }, TestContext.CancellationToken);

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

        // WatchUsnJournal ends quietly on cancellation, so a timeout is the enumeration finishing
        // (or cancelling) without the delete-close record, not only an OperationCanceledException.
        try
        {
            await watchTask;
        }
        catch (OperationCanceledException) when (!TestContext.CancellationToken.IsCancellationRequested)
        {
        }

        Assert.IsTrue(sawDeleteClose,
            $"Timed out waiting for the delete-close record of {tempFileName} after {DeleteCloseWaitSeconds} s. "
            + $"Records seen for the file: {DescribeRecords(batches, fileReference)}");

        // Premise guard: the fix targets the record shape NTFS writes. Without both an
        // intermediate create record and a create-close record in the raw stream, the
        // replay below would prove nothing.
        var fileEntries = batches.SelectMany(batch => batch)
            .Where(e => IsTrackedFile(e, fileReference))
            .ToArray();
        Assert.Contains(e => e.IsCreate && !e.IsClose, fileEntries,
            "Expected an intermediate FileCreate record before the close record.");
        Assert.Contains(e => e.IsCreate && e.IsClose, fileEntries,
            "Expected a FileCreate|Close record.");
        if (holdShareDeleteHandleAcrossDelete)
        {
            Assert.Contains(e => IsDeletePendingRename(e, fileReference), fileEntries,
                "Expected a RenameNewName record outside the original parent (POSIX delete-pending move).");
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
                    ParentRecordNumber = e.ParentRecordNumber == fileReference!.Value.ParentRecordNumber
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

        var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", tempFileName);
        Assert.DoesNotContain(change => change.Kind == FileChangeKind.Renamed, changes,
            "A POSIX delete-pending move into $Extend\\$Deleted must never report Renamed.");
        Assert.ContainsSingle(change => change.Kind == FileChangeKind.Created,
            changes, "One real create must raise exactly one Created change.");
        Assert.ContainsSingle(change => change.Kind == FileChangeKind.Modified,
            changes, "One real append/modify cycle must raise exactly one Modified change.");
        Assert.ContainsSingle(change => change.Kind == FileChangeKind.Deleted,
            changes, "One real delete must raise exactly one Deleted change.");
        Assert.AreEqual(FileChangeKind.Deleted, changes[^1].Kind,
            "The change stream for the deleted file must end with Deleted.");
        if (holdShareDeleteHandleAcrossDelete)
        {
            Assert.ContainsSingle(change => change.Kind == FileChangeKind.DeletePending,
                changes, "A delete while a handle is held must raise exactly one DeletePending change.");
            Assert.AreSequenceEqual(
                [FileChangeKind.Created, FileChangeKind.Modified, FileChangeKind.DeletePending, FileChangeKind.Deleted],
                [.. changes.Select(change => change.Kind)]);
        }

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

    private static bool IsTrackedFile(UsnJournalEntry entry, FileReference? fileReference)
    {
        return fileReference is { } reference
            && entry.RecordNumber == reference.RecordNumber && entry.SequenceNumber == reference.SequenceNumber;
    }

    private static bool IsDeletePendingRename(UsnJournalEntry entry, FileReference? fileReference)
    {
        return fileReference is { } reference
            && (entry.Reason & UsnReason.RenameNewName) != 0 && !entry.IsDelete
            && entry.ParentRecordNumber != reference.ParentRecordNumber;
    }

    private static string DescribeRecords(List<UsnJournalEntry[]> batches, FileReference? fileReference)
    {
        var descriptions = batches.SelectMany(batch => batch)
            .Where(e => IsTrackedFile(e, fileReference))
            .Select(e => $"[Usn={e.Usn}, Name={e.FileName}, ParentRecordNumber={e.ParentRecordNumber}, TimestampUtc={e.TimestampUtc:O}, Reason={e.Reason}, "
                + $"IsClose={e.IsClose}, IsDelete={e.IsDelete}]")
            .ToArray();
        return descriptions.Length == 0 ? "none" : string.Join(" ", descriptions);
    }

    public TestContext TestContext { get; set; } = null!;
}
