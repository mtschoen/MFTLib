using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A <see cref="FileEntry" /> is bound to the file incarnation it was minted for: the row's sequence
///     number, captured at construction. A journal create that reuses a deleted record's row slot installs
///     a new incarnation, and every handle to the old one stops aliasing the new file.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileEntryIncarnationTests
{
    const uint DirectoryRow = 6;
    const uint ChildRow = 7;
    const uint NewChildRow = 8;
    const ushort DirectorySequence = 10;
    const ushort ChildSequence = 20;
    const ushort ReplacementSequence = 11;

    static MutatorFixture SeededFixture()
    {
        var fixture = new MutatorFixture();
        fixture.Block.SequenceNumbers[(int)DirectoryRow] = DirectorySequence;
        fixture.Block.SequenceNumbers[(int)ChildRow] = ChildSequence;
        return fixture;
    }

    /// <summary>
    ///     The audited reproduction: delete the child, delete the directory, create a replacement directory in
    ///     the freed slot, and create a new child, all in one batch.
    /// </summary>
    static IReadOnlyList<FileChange> ApplyReuseBatch(MutatorFixture fixture) => fixture.Apply(
    [
        Record(ChildRow, UsnReason.FileDelete | UsnReason.Close, "notes.txt", DirectoryRow, ChildSequence),
        Record(DirectoryRow, UsnReason.FileDelete | UsnReason.Close, "documents", 5, DirectorySequence,
            FileAttributes.Directory),
        Record(DirectoryRow, UsnReason.FileCreate | UsnReason.Close, "replacement", 5, ReplacementSequence,
            FileAttributes.Directory),
        Record(NewChildRow, UsnReason.FileCreate | UsnReason.Close, "replacement-child.txt", DirectoryRow, 1)
    ]);

    static UsnJournalEntry Record(uint record, UsnReason reason, string name, ulong parent, ushort sequence,
        FileAttributes attributes = FileAttributes.Archive) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            FileName = name,
            Reason = reason,
            FileAttributes = attributes,
            SequenceNumber = sequence,
            Usn = 1000,
            TimestampUtc = new DateTime(2026, 10, 14, 6, 0, 0, DateTimeKind.Utc)
        });

    [TestMethod]
    public async Task RetainedHandle_AfterSlotReuse_ReadsDeletedAndRefusesColumnReads()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        Assert.IsFalse(retained.IsDeleted);

        ApplyReuseBatch(fixture);

        Assert.IsTrue(retained.IsDeleted);
        var reads = new Dictionary<string, Func<object?>>
        {
            [nameof(FileEntry.Name)] = () => retained.Name,
            [nameof(FileEntry.Path)] = () => retained.Path,
            [nameof(FileEntry.Parent)] = () => retained.Parent,
            [nameof(FileEntry.Size)] = () => retained.Size,
            [nameof(FileEntry.IsSizeKnown)] = () => retained.IsSizeKnown,
            [nameof(FileEntry.LastWriteTime)] = () => retained.LastWriteTime,
            [nameof(FileEntry.IsLastWriteTimeKnown)] = () => retained.IsLastWriteTimeKnown,
            [nameof(FileEntry.Attributes)] = () => retained.Attributes,
            [nameof(FileEntry.IsDirectory)] = () => retained.IsDirectory
        };
        foreach (var (column, read) in reads)
        {
            var exception = Assert.ThrowsException<InvalidOperationException>(() => read(), column);
            StringAssert.Contains(exception.Message, "reused after deletion", column);
        }

        Assert.AreEqual(DirectoryRow, (uint)retained.RecordKey.RecordNumber);
        Assert.AreEqual("replacement", FileEntry.Create(fixture.Snapshot, 0, DirectoryRow).Name);
    }

    [TestMethod]
    public async Task DeletedNotification_WhoseSlotWasReused_ReportsDeletedWithTheCapturedPath()
    {
        await using var fixture = SeededFixture();

        var changes = ApplyReuseBatch(fixture);

        var deleted = changes.Single(change => change.Kind == FileChangeKind.Deleted &&
                                               change.Entry.RecordKey.RecordNumber == DirectoryRow);
        Assert.IsTrue(deleted.Entry.IsDeleted);
        StringAssert.EndsWith(deleted.Path, "documents");
        Assert.ThrowsException<InvalidOperationException>(() => deleted.Entry.Name);
    }

    [TestMethod]
    public async Task Equality_NeverMatchesTheReplacementThatReusedTheSlot()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        var sameIncarnation = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        Assert.IsTrue(retained.Equals(sameIncarnation));
        Assert.IsTrue(retained == sameIncarnation);
        Assert.AreEqual(retained.GetHashCode(), sameIncarnation.GetHashCode());

        ApplyReuseBatch(fixture);
        var replacement = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);

        Assert.IsFalse(retained.Equals(replacement));
        Assert.IsFalse(retained.Equals((object)replacement));
        Assert.IsFalse(retained == replacement);
        Assert.IsTrue(retained != replacement);
        Assert.AreNotEqual(retained.GetHashCode(), replacement.GetHashCode());
        Assert.IsTrue(retained == sameIncarnation, "The superseded handle still equals its own incarnation's handle.");
    }

    [TestMethod]
    public async Task Equality_ComparesTheDriveBlockNotTheSnapshot()
    {
        await using var fixture = SeededFixture();
        var first = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        var secondSnapshot = Snapshot.Create([fixture.Snapshot.GetDriveBlock(0)]);
        try
        {
            var second = FileEntry.Create(secondSnapshot, 0, DirectoryRow);

            Assert.AreEqual(first, second);
            Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
            Assert.AreNotEqual(first, default);
            Assert.AreEqual(default, default(FileEntry));
        }
        finally
        {
            await secondSnapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Open_OnASupersededHandle_ThrowsWithoutCallingOpenById()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        var calls = new List<(uint Record, ushort Sequence)>();
        using var restore = FileEntry.OverrideOpenByIdForTest((_, record, sequence, _) =>
        {
            calls.Add((record, sequence));
            return new MemoryBackedFileStream();
        });

        using (retained.Open(FileAccess.Read))
        {
        }

        Assert.AreEqual((DirectoryRow, DirectorySequence), calls.Single());

        ApplyReuseBatch(fixture);
        calls.Clear();

        var exception = Assert.ThrowsException<FileNotFoundException>(() => retained.Open(FileAccess.Read));
        Assert.AreEqual($"{retained}: The entry has been deleted.", exception.Message);
        Assert.AreEqual(0, calls.Count);

        var replacement = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        using (replacement.Open(FileAccess.Read))
        {
        }

        Assert.AreEqual((DirectoryRow, ReplacementSequence), calls.Single());
    }

    [TestMethod]
    public async Task Children_OfASupersededDirectory_IsEmptyWhileTheReplacementListsItsOwn()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        Assert.AreEqual(1, retained.Children().Count);

        ApplyReuseBatch(fixture);

        Assert.AreEqual(0, retained.Children().Count);
        var replacement = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        Assert.AreEqual("replacement-child.txt", replacement.Children().Single().Name);
    }

    [TestMethod]
    public async Task Rename_OfTheSameIncarnation_KeepsTheRetainedHandleLive()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, ChildRow);

        fixture.Apply([Record(ChildRow, UsnReason.RenameNewName | UsnReason.Close, "renamed.txt", DirectoryRow,
            ChildSequence)]);

        Assert.IsFalse(retained.IsDeleted);
        Assert.AreEqual("renamed.txt", retained.Name);
        Assert.AreEqual(FileEntry.Create(fixture.Snapshot, 0, ChildRow), retained);
    }

    [TestMethod]
    public async Task DeleteOfTheSameIncarnation_ReadsAsATombstoneWithoutThrowing()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, ChildRow);

        fixture.Apply([Record(ChildRow, UsnReason.FileDelete | UsnReason.Close, "notes.txt", DirectoryRow,
            ChildSequence)]);

        Assert.IsTrue(retained.IsDeleted);
        Assert.AreEqual("notes.txt", retained.Name);
    }

    [TestMethod]
    public async Task ReplayedCreateOfALiveRow_IsARefreshThatKeepsRetainedHandlesAndTheRealSequence()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, DirectoryRow);
        var calls = new List<ushort>();
        using var restore = FileEntry.OverrideOpenByIdForTest((_, _, sequence, _) =>
        {
            calls.Add(sequence);
            return new MemoryBackedFileStream();
        });

        fixture.Apply([Record(DirectoryRow, UsnReason.FileCreate | UsnReason.Close, "documents", 5,
            DirectorySequence, FileAttributes.Directory)]);

        Assert.IsFalse(retained.IsDeleted);
        Assert.AreEqual("documents", retained.Name);
        Assert.AreEqual(retained, FileEntry.Create(fixture.Snapshot, 0, DirectoryRow));
        using (FileEntry.Create(fixture.Snapshot, 0, DirectoryRow).Open(FileAccess.Read))
        {
        }

        CollectionAssert.AreEqual(new[] { DirectorySequence }, calls);
    }

    [TestMethod]
    public async Task RepeatedReuseWithRisingSequences_NeverAliasesAnEarlierHandle()
    {
        await using var fixture = SeededFixture();
        var original = FileEntry.Create(fixture.Snapshot, 0, ChildRow);
        var handles = new List<FileEntry> { original };
        for (var reuse = 0; reuse < 3; reuse++)
        {
            var next = (ushort)(ChildSequence + reuse);
            fixture.Apply([
                Record(ChildRow, UsnReason.FileDelete | UsnReason.Close, "notes.txt", DirectoryRow, next),
                Record(ChildRow, UsnReason.FileCreate | UsnReason.Close, "again.txt", DirectoryRow, (ushort)(next + 1))
            ]);
            handles.Add(FileEntry.Create(fixture.Snapshot, 0, ChildRow));
        }

        for (var index = 0; index < handles.Count - 1; index++)
        {
            Assert.IsTrue(handles[index].IsDeleted, $"handle {index} is superseded");
            Assert.AreNotEqual(handles[index], handles[^1], $"handle {index} never equals the live one");
            var superseded = handles[index];
            Assert.ThrowsException<InvalidOperationException>(() => superseded.Name);
        }

        Assert.IsFalse(handles[^1].IsDeleted);
        Assert.AreEqual("again.txt", handles[^1].Name);
        Assert.AreEqual(handles.Count, handles.Distinct().Count());
    }

    [DataTestMethod]
    [DataRow(true, DisplayName = "one batch")]
    [DataRow(false, DisplayName = "separate batches")]
    public async Task RetainedOrdinaryFile_AndItsDeletedNotification_AreSupersededByTheReplacement(bool oneBatch)
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, ChildRow);
        var delete = Record(ChildRow, UsnReason.FileDelete | UsnReason.Close, "notes.txt", DirectoryRow,
            ChildSequence);
        var create = Record(ChildRow, UsnReason.FileCreate | UsnReason.Close, "replacement.txt", DirectoryRow,
            ChildSequence + 1);

        FileChange deleted;
        if (oneBatch)
        {
            deleted = fixture.Apply([delete, create]).Single(change => change.Kind == FileChangeKind.Deleted);
        }
        else
        {
            deleted = fixture.Apply([delete]).Single();
            Assert.IsTrue(deleted.Entry.IsDeleted);
            Assert.AreEqual("notes.txt", deleted.Entry.Name, "Before the reuse the notification is a readable tombstone.");
            fixture.Apply([create]);
        }

        var replacement = FileEntry.Create(fixture.Snapshot, 0, ChildRow);
        foreach (var handle in new[] { retained, deleted.Entry })
        {
            Assert.IsTrue(handle.IsDeleted);
            Assert.AreNotEqual(replacement, handle);
            Assert.ThrowsException<InvalidOperationException>(() => handle.Size);
            Assert.AreEqual(0, handle.Children().Count);
        }

        StringAssert.EndsWith(deleted.Path, "notes.txt");
        Assert.AreEqual("replacement.txt", replacement.Name);
    }

    [TestMethod]
    public async Task ReplayOfAnEarlierDeleteAndTheLaterCreate_LeavesTheScannedRowLiveAndOpenable()
    {
        await using var fixture = SeededFixture();
        fixture.Block.SequenceNumbers[(int)ChildRow] = 11;
        var retained = FileEntry.Create(fixture.Snapshot, 0, ChildRow);
        var calls = new List<ushort>();
        using var restore = FileEntry.OverrideOpenByIdForTest((_, _, sequence, _) =>
        {
            calls.Add(sequence);
            return new MemoryBackedFileStream();
        });

        var changes = fixture.Apply([
            Record(ChildRow, UsnReason.FileDelete | UsnReason.Close, "notes.txt", DirectoryRow, 10),
            Record(ChildRow, UsnReason.FileCreate | UsnReason.Close, "notes.txt", DirectoryRow, 11)
        ]);

        Assert.IsFalse(retained.IsDeleted);
        Assert.IsFalse(changes.Any(change => change.Kind == FileChangeKind.Deleted), "The earlier incarnation's delete is ignored.");
        Assert.AreEqual(retained, FileEntry.Create(fixture.Snapshot, 0, ChildRow));
        using (retained.Open(FileAccess.Read))
        {
        }

        CollectionAssert.AreEqual(new ushort[] { 11 }, calls);
    }

    [TestMethod]
    public async Task FreedRecordImportedWithTheNextSequence_IsTheSameReferenceTheNextCreateArrivesWith()
    {
        await using var fixture = SeededFixture();
        fixture.Apply([Record(ChildRow, UsnReason.FileDelete | UsnReason.Close, "notes.txt", DirectoryRow, ChildSequence)]);
        fixture.Block.SequenceNumbers[(int)ChildRow] = ChildSequence + 1;

        fixture.Apply([Record(ChildRow, UsnReason.FileCreate | UsnReason.Close, "next.txt", DirectoryRow,
            ChildSequence + 1)]);

        var live = FileEntry.Create(fixture.Snapshot, 0, ChildRow);
        Assert.AreEqual((ushort)(ChildSequence + 1), fixture.Block.SequenceNumbers[(int)ChildRow]);
        Assert.AreEqual("next.txt", live.Name);
    }

    [TestMethod]
    public async Task LiveRowReplacedWithAHigherSequence_SupersedesTheRetainedHandle()
    {
        await using var fixture = SeededFixture();
        var retained = FileEntry.Create(fixture.Snapshot, 0, ChildRow);

        fixture.Apply([Record(ChildRow, UsnReason.FileCreate | UsnReason.Close, "other.txt", DirectoryRow,
            ChildSequence + 1)]);

        Assert.IsTrue(retained.IsDeleted);
        Assert.AreNotEqual(retained, FileEntry.Create(fixture.Snapshot, 0, ChildRow));
    }

    [TestMethod]
    public async Task SequenceWrap_StoresTheRealSequenceForOpenById()
    {
        await using var fixture = SeededFixture();
        fixture.Block.SequenceNumbers[(int)ChildRow] = ushort.MaxValue;
        var calls = new List<ushort>();
        using var restore = FileEntry.OverrideOpenByIdForTest((_, _, sequence, _) =>
        {
            calls.Add(sequence);
            return new MemoryBackedFileStream();
        });
        var retained = FileEntry.Create(fixture.Snapshot, 0, ChildRow);

        fixture.Apply([
            Record(ChildRow, UsnReason.FileDelete | UsnReason.Close, "notes.txt", DirectoryRow, ushort.MaxValue),
            Record(ChildRow, UsnReason.FileCreate | UsnReason.Close, "wrapped.txt", DirectoryRow, 1)
        ]);

        var replacement = FileEntry.Create(fixture.Snapshot, 0, ChildRow);
        using (replacement.Open(FileAccess.Read))
        {
        }

        Assert.IsTrue(retained.IsDeleted);
        CollectionAssert.AreEqual(new ushort[] { 1 }, calls);
    }
}
