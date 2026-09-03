using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchTests
{
    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    string _treeRoot = null!;
    string _cacheDirectory = null!;
    FileIndex _index = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");

        // Seed a warm-start MFT block for drive 'T' so journal mutations are supported on it.
        Directory.CreateDirectory(_cacheDirectory);
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        using (var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = blockPath,
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Mft,
            SlotCapacity = 256,
            NamePoolCapacity = 4096
        }))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 16, 0, ChangeMoment.Ticks));
            writer.TryWriteRow(1, "Documents", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 16, 0, ChangeMoment.Ticks));
            writer.TryWriteRow(2, "readme.md", new RowColumns(1, RowFlags.InUse, 32, 100, ChangeMoment.Ticks));
            writer.Complete(ChangeMoment);
        }

        _index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives =
            [
                new IndexedDrive('T', _treeRoot, 0x0BADF00D),
                new IndexedDrive('E', _treeRoot, 0x0E0E0E0E),
                new IndexedDrive('Z', Path.Combine(_treeRoot, "does-not-exist"), 0xDEAD0000)
            ],
            CacheDirectory = _cacheDirectory
        }, CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _index.DisposeAsync();
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    static UsnJournalEntry Entry(ulong recordNumber, ulong parentRecordNumber, string fileName,
        UsnReason reason)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            Usn = 1,
            Timestamp = ChangeMoment,
            Reason = reason,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    [TestMethod]
    public async Task StartWatchingAsync_OnAnEnumerationDrive_CompletesWithoutStartingAWatch()
    {
        await _index.StartWatchingAsync(CancellationToken.None);
        Assert.IsFalse(_index.Drives[1].WatchSupported);
        Assert.IsTrue(_index.Drives[0].WatchSupported);
    }

    [TestMethod]
    public void ApplyJournalEntries_OnEnumerationDrive_ThrowsInvalidOperation()
    {
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => _index.ApplyJournalEntries('E', [Entry(20, 0, "injected.txt", UsnReason.FileCreate | UsnReason.Close)], 5, 100));

        StringAssert.Contains(exception.Message, "enumeration producer");
    }

    [TestMethod]
    public void ApplyJournalEntries_CreatesARowAndRaisesChanged()
    {
        var observed = new List<FileChange>();
        _index.Changed += observed.Add;

        var applied = _index.ApplyJournalEntries('T',
            [Entry(20, 0, "injected.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalId: 5, nextUsn: 100);

        Assert.AreEqual(1, applied.Count);
        Assert.AreEqual(1, observed.Count);
        Assert.AreEqual(FileChangeKind.Created, observed[0].Kind);
        Assert.AreEqual("injected.txt", observed[0].Entry.Name);
    }

    [TestMethod]
    public void ApplyJournalEntries_MakesTheNewRowVisibleToSearch()
    {
        _index.ApplyJournalEntries('T',
            [Entry(20, 0, "injected.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalId: 5, nextUsn: 100);

        Assert.AreEqual(1, _index.Search(new SearchQuery("injected")).Count);
    }

    [TestMethod]
    public void ApplyJournalEntries_DeleteMakesTheEntryReadAsDeletedButKeepsItsName()
    {
        var target = _index.Find(@"T:\Documents\readme.md")!.Value;
        var recordNumber = target.Id.RecordNumber;

        var applied = _index.ApplyJournalEntries('T',
            [Entry(recordNumber, 0, "readme.md", UsnReason.FileDelete | UsnReason.Close)],
            journalId: 5, nextUsn: 200);

        Assert.AreEqual(FileChangeKind.Deleted, applied[0].Kind);
        Assert.IsTrue(target.IsDeleted);
        Assert.AreEqual("readme.md", target.Name);
    }

    [TestMethod]
    public void ApplyJournalEntries_ExhaustingCapacityReportsTheDriveStale()
    {
        _index.ApplyJournalEntries('T',
            [Entry(50_000_000, 0, "far.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalId: 5, nextUsn: 300);

        Assert.AreEqual(DriveState.Stale, _index.Drives[0].State);
        Assert.IsTrue(_index.Drives[0].CompactionNeeded);
    }

    [TestMethod]
    public void ApplyJournalEntries_UnknownDriveThrows()
    {
        Assert.ThrowsException<ArgumentException>(() => _index.ApplyJournalEntries('Q', [], 0, 0));
    }

    [TestMethod]
    public void ApplyJournalEntries_ConfiguredButOfflineDriveThrowsInvalidOperation()
    {
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => _index.ApplyJournalEntries('Z', [], 0, 0));

        StringAssert.Contains(exception.Message, "offline");
    }

    [TestMethod]
    public void ApplyJournalEntries_HandlerException_StillDeliversEveryChangeToOtherSubscribersThenSurfaces()
    {
        var firstSubscriberCallCount = 0;
        _index.Changed += _ =>
        {
            firstSubscriberCallCount++;
            if (firstSubscriberCallCount == 1)
            {
                throw new InvalidOperationException("boom");
            }
        };

        var secondSubscriberChanges = new List<FileChange>();
        _index.Changed += secondSubscriberChanges.Add;

        var thrown = Assert.ThrowsException<InvalidOperationException>(() => _index.ApplyJournalEntries('T',
            [
                Entry(20, 0, "first.txt", UsnReason.FileCreate | UsnReason.Close),
                Entry(21, 0, "second.txt", UsnReason.FileCreate | UsnReason.Close)
            ],
            journalId: 5, nextUsn: 100));

        Assert.AreEqual("boom", thrown.Message);
        Assert.AreEqual(2, secondSubscriberChanges.Count);
        CollectionAssert.AreEquivalent(new[] { "first.txt", "second.txt" },
            secondSubscriberChanges.Select(change => change.Entry.Name).ToArray());
    }

    [TestMethod]
    public void ApplyJournalEntries_SingleHandlerException_KeepsTheStackTraceOfTheHandlerThatThrew()
    {
        _index.Changed += ThrowingSubscriber;

        var thrown = Assert.ThrowsException<InvalidOperationException>(() => _index.ApplyJournalEntries('T',
            [Entry(20, 0, "first.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalId: 5, nextUsn: 100));

        // Rethrowing the caught instance directly would replace this frame with the throw site
        // inside MFTLib, leaving a consumer no way to find the handler that actually failed.
        StringAssert.Contains(thrown.StackTrace ?? string.Empty, nameof(ThrowingSubscriber));
    }

    static void ThrowingSubscriber(FileChange change)
    {
        throw new InvalidOperationException("boom");
    }
}
