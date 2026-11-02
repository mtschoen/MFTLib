using MFTLib.Index;
using MFTLib.Tests.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Replays the USN records a live watch on a real volume actually delivers for a
///     create-modify-delete cycle through the mutator: the raw stream contains the
///     intermediate record and the close record for every operation, and the reported
///     change stream must contain exactly one change per real transition. Record numbers
///     are remapped onto one synthetic row so the replay needs no multi-gigabyte block;
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

    private readonly record struct FileReference(ulong RecordNumber, ushort SequenceNumber, ulong ParentRecordNumber);

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public Task LiveWatch_CreateModifyDeleteCycle_ReportsOneChangePerRealTransition()
    {
        return RunCreateModifyDeleteCycleAsync(holdShareDeleteHandleAcrossDelete: false);
    }

    // A share-delete handle held across the delete forces the POSIX delete-pending path every time:
    // NTFS moves the file into \$Extend\$Deleted (RenameOldName/RenameNewName under a different parent)
    // before the FileDelete|Close record, instead of leaving it to a first-run scanner.
    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public Task LiveWatch_DeleteWhileHandleHeld_ReportsOneChangePerRealTransition()
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
        var changes = new List<FileChange>();
        DateTime? lastContentClose = null;
        long ticksAfterLastContentClose = 0;
        foreach (var batch in batches)
        {
            var replayed = batch
                .Where(e => IsTrackedFile(e, fileReference) && !IsDeletePendingRename(e, fileReference))
                .Select(e => UsnJournalEntry.Create(new UsnJournalEntryOptions
                {
                    RecordNumber = 20,
                    ParentRecordNumber = 6,
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
                    ticksAfterLastContentClose = fixture.Block.Rows[20].ModifiedTicks;
                }
            }
        }

        Assert.ContainsSingle(change => change.Kind == FileChangeKind.Created,
changes, "One real create must raise exactly one Created change.");
        Assert.ContainsSingle(change => change.Kind == FileChangeKind.Modified,
changes, "One real append/modify cycle must raise exactly one Modified change.");
        Assert.ContainsSingle(change => change.Kind == FileChangeKind.Deleted,
changes, "One real delete must raise exactly one Deleted change.");
        Assert.IsTrue(fixture.Block.Rows[20].IsDeleted);
        Assert.IsNotNull(lastContentClose, "Expected a close record for the create-write cycle.");
        Assert.AreEqual(0L, ticksAfterLastContentClose,
            "The close record's timestamp must never be written into the last-write column.");
        Assert.IsFalse(fixture.Block.Rows[20].LastWriteTimeKnown,
            "The create-write cycle invalidated the last-write observation and no scan restored it.");
    }

    private static bool IsTrackedFile(UsnJournalEntry entry, FileReference? fileReference)
    {
        return fileReference is { } reference
            && entry.RecordNumber == reference.RecordNumber && entry.SequenceNumber == reference.SequenceNumber;
    }

    // POSIX delete-pending: the RenameNewName half of the move into \$Extend\$Deleted lands outside the
    // original parent. It is an implementation detail of the delete, not a user rename, and the replay
    // remaps every parent onto one synthetic directory, so it would surface as a spurious rename change.
    // The FileDelete|Close record that follows is kept and still drives the single Deleted change.
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
