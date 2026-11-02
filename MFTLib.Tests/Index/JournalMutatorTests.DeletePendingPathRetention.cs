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

            var pendingChanges = mutator.Apply(rootBlock,
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

            var ancestorRenameChanges = mutator.Apply(rootBlock,
            [
                CreateEntry(UserDirectoryRow, VolumeRootRow, "documents", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1, FileAttributes.Directory),
                CreateEntry(UserDirectoryRow, VolumeRootRow, "archive", UsnReason.RenameNewName | UsnReason.Close,
                    ChangeMoment, sequenceNumber: 1, FileAttributes.Directory)
            ], journalIdentifier: 7, nextUsn: 2001);
            Assert.HasCount(1, ancestorRenameChanges);
            Assert.AreEqual(FileChangeKind.Renamed, ancestorRenameChanges[0].Kind);
            Assert.AreEqual(expectedPath, pendingChanges[0].Entry.Path);

            var deletedChanges = mutator.Apply(rootBlock,
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
    public async Task DeletePending_AncestorDeletedAndSlotReusedBetweenEvents_RetainsOriginalPathAndNameOnDeleted()
    {
        var (directory, block, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            var mutator = new JournalMutator(writer);

            var pendingChanges = mutator.Apply(rootBlock,
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

            var ancestorChurnChanges = mutator.Apply(rootBlock,
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

            var deletedChanges = mutator.Apply(rootBlock,
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
        var blockPath = Path.Combine(directory.Path, "T-0BADF00D.mlix");
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
                var pendingChanges = initialMutator.Apply(initialRootBlock,
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
            var ancestorRenameChanges = reopenedMutator.Apply(reopenedRootBlock,
            [
                CreateEntry(UserDirectoryRow, VolumeRootRow, "documents", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1, FileAttributes.Directory),
                CreateEntry(UserDirectoryRow, VolumeRootRow, "archive", UsnReason.RenameNewName | UsnReason.Close,
                    ChangeMoment, sequenceNumber: 1, FileAttributes.Directory)
            ], journalIdentifier: 7, nextUsn: 2001);
            Assert.HasCount(1, ancestorRenameChanges);
            Assert.AreEqual(FileChangeKind.Renamed, ancestorRenameChanges[0].Kind);

            var deletedChanges = reopenedMutator.Apply(reopenedRootBlock,
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
        var blockPath = Path.Combine(directory.Path, "T-0BADF00D.mlix");
        using var block = TestBlocks.Create(blockPath, ProducerKind.Mft, slotCapacity: 32, namePoolCapacity: 512,
            rootRow: VolumeRootRow);
        var writer = new BlockWriter(block);
        SeedExtendDeletedTree(writer);
        var remainingPoolBytes = (int)(block.NamePoolCapacity - block.NamePoolUsed);
        Assert.IsTrue(writer.TryWriteRow(20, new string('f', remainingPoolBytes),
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
            RowFlags? flagsAfterRename = null;
            string? nameAfterRename = null;
            uint? liveRowCountAfterRename = null;
            writer.Hooks._rowCaptured = () =>
            {
                flagsAfterRename = writer.Block.Rows[(int)NotesFileRow].Flags;
                nameAfterRename = NamePool.ReadRowName(writer.Block, NotesFileRow).ToString();
                liveRowCountAfterRename = writer.Block.Header.LiveRowCount;
            };

            var mutator = new JournalMutator(writer);
            var changes = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2000);

            Assert.DoesNotContain(change => change.Kind == FileChangeKind.DeletePending, changes);
            Assert.HasCount(1, changes);
            Assert.AreEqual(FileChangeKind.Deleted, changes[0].Kind);
            Assert.AreEqual(expectedPath, changes[0].Path);
            Assert.AreEqual("notes.txt", changes[0].Entry.Name);
            Assert.IsNull(changes[0].PreviousPath);
            Assert.AreEqual(RowFlags.InUse, flagsAfterRename);
            Assert.AreEqual("notes.txt", nameAfterRename);
            Assert.AreEqual(liveRowCountBefore, liveRowCountAfterRename);
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
        var blockPath = Path.Combine(directory.Path, "T-0BADF00D.mlix");
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
            RowFlags? flagsAfterRename = null;
            string? nameAfterRename = null;
            uint? liveRowCountAfterRename = null;
            writer.Hooks._rowCaptured = () =>
            {
                flagsAfterRename = writer.Block.Rows[(int)NotesFileRow].Flags;
                nameAfterRename = NamePool.ReadRowName(writer.Block, NotesFileRow).ToString();
                liveRowCountAfterRename = writer.Block.Header.LiveRowCount;
            };

            var mutator = new JournalMutator(writer);
            var changes = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1, FileAttributes.Archive | FileAttributes.ReadOnly),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1,
                    FileAttributes.Archive | FileAttributes.ReadOnly)
            ], journalIdentifier: 7, nextUsn: 2000);

            Assert.DoesNotContain(change => change.Kind == FileChangeKind.DeletePending, changes);
            Assert.HasCount(1, changes);
            Assert.AreEqual(FileChangeKind.Deleted, changes[0].Kind);
            Assert.AreEqual(expectedPath, changes[0].Path);
            Assert.AreEqual("notes.txt", changes[0].Entry.Name);
            Assert.IsNull(changes[0].PreviousPath);
            Assert.AreEqual(RowFlags.InUse, flagsAfterRename);
            Assert.AreEqual("notes.txt", nameAfterRename);
            Assert.AreEqual(liveRowCountBefore, liveRowCountAfterRename);
            Assert.IsTrue(block.Header.IsCapacityExhausted);
            Assert.AreEqual((uint)(FileAttributes.Archive | FileAttributes.ReadOnly),
                block.Rows[(int)NotesFileRow].Attributes);
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
