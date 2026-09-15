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
        if (!ElevationUtilities.IsElevated())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"mftlib-close-test-{Guid.NewGuid():N}.tmp");
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(tempPath));
        var driveSpecifier = !string.IsNullOrEmpty(volumeRoot) && volumeRoot.Length >= 2 && volumeRoot[1] == ':'
            ? volumeRoot[..1]
            : "C";
        using var volume = MftVolume.Open(driveSpecifier);
        var cursor = volume.QueryUsnJournal();
        var tempFileName = Path.GetFileName(tempPath);

        var batches = new List<UsnJournalEntry[]>();
        var watchTask = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // ReSharper disable once AccessToDisposedClosure
            // volume is captured from outer scope; watchTask is awaited before volume's using exits.
            await foreach (var batch in volume.WatchUsnJournal(cursor, timeout.Token))
            {
                batches.Add(batch);
                if (batch.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase)
                        && e.IsDelete && e.IsClose))
                {
                    break;
                }
            }
        });

        await File.WriteAllTextAsync(tempPath, "create", CancellationToken.None);
        await File.AppendAllTextAsync(tempPath, "modify", CancellationToken.None);
        File.Delete(tempPath);

        try
        {
            await watchTask;
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Timed out waiting for the delete-close record of {tempFileName}.");
        }

        // Premise guard: the fix targets the record shape NTFS writes. Without both an
        // intermediate create record and a create-close record in the raw stream, the
        // replay below would prove nothing.
        var fileEntries = batches.SelectMany(batch => batch)
            .Where(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.IsTrue(fileEntries.Any(e => e.IsCreate && !e.IsClose),
            "Expected an intermediate FileCreate record before the close record.");
        Assert.IsTrue(fileEntries.Any(e => e.IsCreate && e.IsClose),
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
                    Timestamp = e.Timestamp,
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
                    lastContentClose = entry.Timestamp;
                    ticksAfterLastContentClose = fixture.Block.Rows[20].ModifiedTicks;
                }
            }
        }

        Assert.AreEqual(1, changes.Count(change => change.Kind == FileChangeKind.Created),
            "One real create must raise exactly one Created change.");
        Assert.AreEqual(1, changes.Count(change => change.Kind == FileChangeKind.Modified),
            "One real append/modify cycle must raise exactly one Modified change.");
        Assert.AreEqual(1, changes.Count(change => change.Kind == FileChangeKind.Deleted),
            "One real delete must raise exactly one Deleted change.");
        Assert.IsTrue(fixture.Block.Rows[20].IsDeleted);
        Assert.IsNotNull(lastContentClose, "Expected a close record for the create-write cycle.");
        Assert.AreEqual(lastContentClose.Value.Ticks, ticksAfterLastContentClose,
            "The row must take the close record's timestamp even though its change was coalesced.");
    }
}
