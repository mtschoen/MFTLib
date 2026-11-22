using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

public partial class JournalMutatorTests
{
    [TestMethod]
    public async Task DeletePending_AncestorRenamedBetweenEvents_RetainsOriginalPathAndNameOnDeleted()
    {
        var (directory, block, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            var mutator = new JournalMutator(writer);

            var pendingChanges = mutator.ApplyWhole(rootBlock,
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2000);

            Assert.HasCount(1, pendingChanges);
            var pendingPath = pendingChanges[0].Path;
            var pendingName = pendingChanges[0].Entry.Name;
            Assert.AreEqual(FileChangeKind.DeletePending, pendingChanges[0].Kind);
            Assert.AreEqual(expectedPath, pendingPath);
            Assert.AreEqual("notes.txt", pendingName);
            Assert.IsNull(PostingIntegrity.Check(block));

            var ancestorRenameChanges = mutator.ApplyWhole(rootBlock,
            [
                CreateEntry(UserDirectoryRow, VolumeRootRow, "documents", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1, FileAttributes.Directory),
                CreateEntry(UserDirectoryRow, VolumeRootRow, "archive", UsnReason.RenameNewName | UsnReason.Close,
                    ChangeMoment, sequenceNumber: 1, FileAttributes.Directory)
            ], journalIdentifier: 7, nextUsn: 2001);
            Assert.HasCount(1, ancestorRenameChanges);
            Assert.AreEqual(FileChangeKind.Renamed, ancestorRenameChanges[0].Kind);
            Assert.AreEqual(expectedPath, pendingChanges[0].Entry.Path);

            var deletedChanges = mutator.ApplyWhole(rootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2002);

            Assert.HasCount(1, deletedChanges);
            Assert.AreEqual(FileChangeKind.Deleted, deletedChanges[0].Kind);
            Assert.AreEqual(pendingPath, deletedChanges[0].Path);
            Assert.AreEqual(pendingName, deletedChanges[0].Entry.Name);
            Assert.IsNull(deletedChanges[0].PreviousPath);
            Assert.IsNull(PostingIntegrity.Check(block));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    [DataRow("notes.txt", null)]
    [DataRow("資料", @"T:\$MFT\documents\資料")]
    public async Task DeletePending_DescendantUsesTerminalNameBeforeAndAfterCompaction(string name, string? historicalPath)
    {
        var (directory, block, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(UserDirectoryRow, "documents",
                new RowColumns(9, RowFlags.InUse | RowFlags.Directory, 0, 0, ChangeMoment.Ticks, 1)));
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(NotesFileRow, name,
                new RowColumns(UserDirectoryRow, RowFlags.InUse | RowFlags.Directory, 0, 0, ChangeMoment.Ticks, 1)));
            const uint survivorRow = 8;
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(survivorRow, "survivor.bin",
                new RowColumns(NotesFileRow, RowFlags.InUse, 0, 3, ChangeMoment.Ticks, 1)));
            var capturedPath = historicalPath ?? Path.Combine("documents", name);
            if (historicalPath is null)
            {
                var changes = new JournalMutator(writer).ApplyWhole(rootBlock,
                [
                    CreateEntry(NotesFileRow, UserDirectoryRow, name, UsnReason.RenameOldName, ChangeMoment,
                        sequenceNumber: 1, FileAttributes.Directory),
                    CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                        ChangeMoment, sequenceNumber: 1, FileAttributes.Directory)
                ], journalIdentifier: 7, nextUsn: 2000);
                Assert.HasCount(1, changes);
                Assert.AreEqual(capturedPath, changes[0].Path);
            }
            else
            {
                Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(NotesFileRow, historicalPath));
            }

            var survivor = FileEntry.Create(rootBlock, survivorRow);
            var expectedPath = Path.Combine("documents", name, "survivor.bin");
            Assert.AreEqual(expectedPath, survivor.Path);
            using var target = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: 64, namePoolCapacity: 8192);
            using var references = NameReferences.Count(block);
            BlockCopier.CopyCompacting(block, target, references, CancellationToken.None);
            rootBlock.Mapped.Replace(target);

            Assert.IsFalse(NamePool.TryFindOrdinal(target, name, out _));
            Assert.AreEqual(capturedPath, FileEntry.Create(rootBlock, NotesFileRow).Path);
            Assert.AreEqual(expectedPath, survivor.Path);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    public async Task DeletePending_AncestorDeletedAndSlotReusedBetweenEvents_RetainsOriginalPathAndNameOnDeleted()
    {
        var (directory, block, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            var mutator = new JournalMutator(writer);

            var pendingChanges = mutator.ApplyWhole(rootBlock,
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2000);

            Assert.HasCount(1, pendingChanges);
            var pendingPath = pendingChanges[0].Path;
            var pendingName = pendingChanges[0].Entry.Name;
            Assert.AreEqual(FileChangeKind.DeletePending, pendingChanges[0].Kind);
            Assert.AreEqual(expectedPath, pendingPath);
            Assert.AreEqual("notes.txt", pendingName);

            var ancestorChurnChanges = mutator.ApplyWhole(rootBlock,
            [
                CreateEntry(UserDirectoryRow, VolumeRootRow, "documents",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1,
                    FileAttributes.Directory),
                CreateEntry(UserDirectoryRow, VolumeRootRow, "replacement",
                    UsnReason.FileCreate | UsnReason.Close, ChangeMoment, sequenceNumber: 2,
                    FileAttributes.Directory)
            ], journalIdentifier: 7, nextUsn: 2001);
            Assert.HasCount(2, ancestorChurnChanges);
            Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[(int)NotesFileRow].ParentRow);

            var deletedChanges = mutator.ApplyWhole(rootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2002);

            Assert.HasCount(1, deletedChanges);
            Assert.AreEqual(FileChangeKind.Deleted, deletedChanges[0].Kind);
            Assert.AreEqual(pendingPath, deletedChanges[0].Path);
            Assert.AreEqual(pendingName, deletedChanges[0].Entry.Name);
            Assert.IsNull(deletedChanges[0].PreviousPath);
            Assert.IsNull(PostingIntegrity.Check(block));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    public async Task DeletePending_BlockReopenedThenAncestorRenamedBeforeFileDelete_RetainsOriginalPathAndNameOnDeleted()
    {
        using var directory = new TempDirectory("mftlib-delete-pending-reopen-rename");
        var blockPath = Path.Combine(directory.Path, "T-000000000BADF00D.mlix");
        var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
        string pendingPath;
        string pendingName;

        using (var initialBlock = TestBlocks.Create(blockPath, ProducerKind.Mft, slotCapacity: 32,
                   namePoolCapacity: 4096, rootRow: VolumeRootRow, deleteOnClose: false))
        {
            var initialWriter = new BlockWriter(initialBlock);
            SeedExtendDeletedTree(initialWriter);
            initialWriter.Complete(Moment, null);
            var initialRootBlock = new RootBlock(new VolumeKey('T'), initialBlock,
                rootDirectoryPath: TestRootDirectory.For('T'));
            RootBlock[] initialSnapshot = [initialRootBlock];
            try
            {
                var initialMutator = new JournalMutator(initialWriter);
                var pendingChanges = initialMutator.ApplyWhole(initialRootBlock,
                [
                    CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                        sequenceNumber: 1),
                    CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                        ChangeMoment, sequenceNumber: 1)
                ], journalIdentifier: 7, nextUsn: 2000);

                Assert.HasCount(1, pendingChanges);
                pendingPath = pendingChanges[0].Path;
                pendingName = pendingChanges[0].Entry.Name;
                Assert.AreEqual(FileChangeKind.DeletePending, pendingChanges[0].Kind);
                Assert.AreEqual(expectedPath, pendingPath);
                Assert.AreEqual("notes.txt", pendingName);
                initialBlock.Flush(null);
            }
            finally
            {
                await initialSnapshot.ReleaseNowAsync();
            }
        }

        using var reopenedBlock = BlockFile.Open(blockPath, 0x0BADF00D, out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(reopenedBlock);
        var reopenedRootBlock = new RootBlock(new VolumeKey('T'), reopenedBlock,
            rootDirectoryPath: TestRootDirectory.For('T'));
        RootBlock[] reopenedSnapshot = [reopenedRootBlock];
        try
        {
            var reopenedMutator = new JournalMutator(new BlockWriter(reopenedBlock));
            var ancestorRenameChanges = reopenedMutator.ApplyWhole(reopenedRootBlock,
            [
                CreateEntry(UserDirectoryRow, VolumeRootRow, "documents", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1, FileAttributes.Directory),
                CreateEntry(UserDirectoryRow, VolumeRootRow, "archive", UsnReason.RenameNewName | UsnReason.Close,
                    ChangeMoment, sequenceNumber: 1, FileAttributes.Directory)
            ], journalIdentifier: 7, nextUsn: 2001);
            Assert.HasCount(1, ancestorRenameChanges);
            Assert.AreEqual(FileChangeKind.Renamed, ancestorRenameChanges[0].Kind);

            var deletedChanges = reopenedMutator.ApplyWhole(reopenedRootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2002);

            Assert.HasCount(1, deletedChanges);
            Assert.AreEqual(FileChangeKind.Deleted, deletedChanges[0].Kind);
            Assert.AreEqual(pendingPath, deletedChanges[0].Path);
            Assert.AreEqual(pendingName, deletedChanges[0].Entry.Name);
            Assert.IsNull(deletedChanges[0].PreviousPath);
            Assert.IsNull(PostingIntegrity.Check(reopenedBlock));
        }
        finally
        {
            await reopenedSnapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task DeletePending_NamePoolExhausted_RefusesPendingTransitionAndCompletesDeletedWithFullPathInSameBatch()
    {
        using var directory = new TempDirectory("mftlib-delete-pending-pool-exhausted");
        var blockPath = Path.Combine(directory.Path, "T-000000000BADF00D.mlix");
        using var block = TestBlocks.Create(blockPath, ProducerKind.Mft, slotCapacity: 32, namePoolCapacity: 512,
            rootRow: VolumeRootRow);
        var writer = new BlockWriter(block);
        SeedExtendDeletedTree(writer);
        var remainingPoolBytes = (int)(block.NamePoolCapacity - block.NamePoolUsed);
        Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(20, new string('f', remainingPoolBytes),
            new RowColumns(ParentRow: VolumeRootRow, RowFlags.InUse, Attributes: 32, Size: 1, Moment.Ticks,
                SequenceNumber: 1)));
        writer.Complete(Moment, null);
        Assert.AreEqual(block.NamePoolCapacity, block.NamePoolUsed);
        Assert.IsFalse(block.Header.IsCapacityExhausted);

        var rootBlock = new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'));
        RootBlock[] snapshot = [rootBlock];
        try
        {
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            var liveRowCountBefore = block.Header.LiveRowCount;
            var attributesBefore = block.Rows[(int)NotesFileRow].Attributes;
            var mutator = new JournalMutator(writer);
            UsnJournalEntry[] entries =
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ];

            // The refused rename stops the batch with the row exactly as it was and the cursor on the refused record.
            var stopped = mutator.ApplyUntilRefusal(rootBlock, entries, 0, journalIdentifier: 7, nextUsn: 2000);
            Assert.AreEqual(1, stopped.Refusal!.Value.Index);
            Assert.AreEqual(entries[1].Usn, block.Header.UsnNextUsn);
            Assert.AreEqual(RowFlags.InUse, block.Rows[(int)NotesFileRow].Flags);
            Assert.AreEqual("notes.txt", NamePool.ReadRowName(block, NotesFileRow).ToString());
            Assert.AreEqual(liveRowCountBefore, block.Header.LiveRowCount);
            Assert.AreEqual(attributesBefore, block.Rows[(int)NotesFileRow].Attributes);
            Assert.IsFalse(block.Header.IsCapacityExhausted);

            // The index flags and skips a record it could not grow for and resumes after it; the delete then
            // completes the row with its full path.
            writer.MarkCapacityExhausted();
            var resumed = mutator.ApplyUntilRefusal(rootBlock, entries, 2, journalIdentifier: 7, nextUsn: 2000);
            Assert.IsNull(resumed.Refusal);
            var changes = resumed.Changes;

            Assert.DoesNotContain(change => change.Kind == FileChangeKind.DeletePending, changes);
            Assert.HasCount(1, changes);
            Assert.AreEqual(FileChangeKind.Deleted, changes[0].Kind);
            Assert.AreEqual(expectedPath, changes[0].Path);
            Assert.AreEqual("notes.txt", changes[0].Entry.Name);
            Assert.IsNull(changes[0].PreviousPath);
            Assert.IsTrue(block.Header.IsCapacityExhausted);
            Assert.AreEqual("notes.txt", NamePool.ReadRowName(block, NotesFileRow).ToString());
            Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, block.Rows[(int)NotesFileRow].Flags);
            Assert.AreEqual(liveRowCountBefore - 1, block.Header.LiveRowCount);
            Assert.IsNull(PostingIntegrity.Check(block));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task DeletePending_NameEntryTableExhausted_RefusesPendingTransitionAndCompletesDeletedWithFullPathInSameBatch()
    {
        using var directory = new TempDirectory("mftlib-delete-pending-entries-exhausted");
        var blockPath = Path.Combine(directory.Path, "T-000000000BADF00D.mlix");
        using var block = TestBlocks.Create(blockPath, ProducerKind.Mft, slotCapacity: 32, namePoolCapacity: 4096,
            nameEntryCapacity: 6, rootRow: VolumeRootRow);
        var writer = new BlockWriter(block);
        SeedExtendDeletedTree(writer);
        writer.Complete(Moment, null);
        Assert.AreEqual(block.NameEntryCapacity, block.NameEntryCount);
        Assert.IsFalse(block.Header.IsCapacityExhausted);

        var rootBlock = new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'));
        RootBlock[] snapshot = [rootBlock];
        try
        {
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            var liveRowCountBefore = block.Header.LiveRowCount;
            var attributesBefore = block.Rows[(int)NotesFileRow].Attributes;
            var mutator = new JournalMutator(writer);
            UsnJournalEntry[] entries =
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1, FileAttributes.Archive | FileAttributes.ReadOnly),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1,
                    FileAttributes.Archive | FileAttributes.ReadOnly)
            ];

            // The refused rename stops the batch with the row exactly as it was and the cursor on the refused record.
            var stopped = mutator.ApplyUntilRefusal(rootBlock, entries, 0, journalIdentifier: 7, nextUsn: 2000);
            Assert.AreEqual(1, stopped.Refusal!.Value.Index);
            Assert.AreEqual(entries[1].Usn, block.Header.UsnNextUsn);
            Assert.AreEqual(RowFlags.InUse, block.Rows[(int)NotesFileRow].Flags);
            Assert.AreEqual("notes.txt", NamePool.ReadRowName(block, NotesFileRow).ToString());
            Assert.AreEqual(liveRowCountBefore, block.Header.LiveRowCount);
            Assert.AreEqual(attributesBefore, block.Rows[(int)NotesFileRow].Attributes);
            Assert.IsFalse(block.Header.IsCapacityExhausted);

            // The index flags and skips a record it could not grow for and resumes after it; the delete then
            // completes the row with its full path.
            writer.MarkCapacityExhausted();
            var resumed = mutator.ApplyUntilRefusal(rootBlock, entries, 2, journalIdentifier: 7, nextUsn: 2000);
            Assert.IsNull(resumed.Refusal);
            var changes = resumed.Changes;

            Assert.DoesNotContain(change => change.Kind == FileChangeKind.DeletePending, changes);
            Assert.HasCount(1, changes);
            Assert.AreEqual(FileChangeKind.Deleted, changes[0].Kind);
            Assert.AreEqual(expectedPath, changes[0].Path);
            Assert.AreEqual("notes.txt", changes[0].Entry.Name);
            Assert.IsNull(changes[0].PreviousPath);
            Assert.IsTrue(block.Header.IsCapacityExhausted);
            Assert.AreEqual(attributesBefore, block.Rows[(int)NotesFileRow].Attributes);
            Assert.AreEqual("notes.txt", NamePool.ReadRowName(block, NotesFileRow).ToString());
            Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, block.Rows[(int)NotesFileRow].Flags);
            Assert.AreEqual(liveRowCountBefore - 1, block.Header.LiveRowCount);
            Assert.IsNull(PostingIntegrity.Check(block));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
