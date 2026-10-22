using MFTLib.Index;
using MFTLib.Tests.Index;
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
    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public async Task LiveWatch_CreateModifyDeleteCycle_ReportsOneChangePerRealTransition()
    {
        if (!ElevationUtilities.DefaultProvider.IsElevated())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"mftlib-close-test-{Guid.NewGuid():N}.tmp");
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(tempPath));
        var drive = !string.IsNullOrEmpty(volumeRoot) && volumeRoot.Length >= 2 && volumeRoot[1] == ':'
            ? new DriveKey(volumeRoot[0])
            : new DriveKey('C');
        using var volume = MftVolume.Open(drive);
        var cursor = volume.Journal.QueryUsnJournalCursor();
        var tempFileName = Path.GetFileName(tempPath);

        var batches = new List<UsnJournalEntry[]>();
        var watchTask = Task.Run(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            // ReSharper disable once AccessToDisposedClosure
            // volume is captured from outer scope; watchTask is awaited before volume's using exits.
            await foreach (var (batch, _) in volume.Journal.WatchUsnJournal(cursor, timeout.Token))
            {
                batches.Add(batch);
                if (batch.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase)
                        && e.IsDelete && e.IsClose))
                {
                    break;
                }
            }
        }, TestContext.CancellationToken);

        await File.WriteAllTextAsync(tempPath, "create", CancellationToken.None);
        await File.AppendAllTextAsync(tempPath, "modify", CancellationToken.None);
        File.Delete(tempPath);

        try
        {
            await watchTask;
        }
        catch (OperationCanceledException) when (!TestContext.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for the delete-close record of {tempFileName}.");
        }

        // Premise guard: the fix targets the record shape NTFS writes. Without both an
        // intermediate create record and a create-close record in the raw stream, the
        // replay below would prove nothing.
        var fileEntries = batches.SelectMany(batch => batch)
            .Where(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Contains(e => e.IsCreate && !e.IsClose, fileEntries,
            "Expected an intermediate FileCreate record before the close record.");
        Assert.Contains(e => e.IsCreate && e.IsClose, fileEntries,
            "Expected a FileCreate|Close record.");

        await using var fixture = new MutatorFixture();
        var changes = new List<FileChange>();
        DateTime? lastContentClose = null;
        long ticksAfterLastContentClose = 0;
        foreach (var batch in batches)
        {
            var replayed = batch
                .Where(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase))
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

    public TestContext TestContext { get; set; } = null!;
}
