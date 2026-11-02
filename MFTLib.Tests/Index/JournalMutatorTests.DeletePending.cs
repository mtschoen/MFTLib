using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

public partial class JournalMutatorTests
{
    const uint VolumeRootRow = 5;
    const uint ExtendDirectoryRow = 11;
    const uint DeletedDirectoryRow = 12;
    const uint UserDirectoryRow = 6;
    const uint NotesFileRow = 7;

    static (TempDirectory Directory, BlockFile Block, BlockWriter Writer, RootBlock RootBlock, RootBlock[] Snapshot)
        CreateExtendDeletedFixture()
    {
        var directory = new TempDirectory("mftlib-delete-pending");
        var blockPath = Path.Combine(directory.Path, "T-0BADF00D.mlix");
        var block = TestBlocks.Create(blockPath, ProducerKind.Mft, slotCapacity: 32, namePoolCapacity: 4096,
            rootRow: VolumeRootRow);
        var writer = new BlockWriter(block);
        SeedExtendDeletedTree(writer);
        writer.Complete(Moment, null);
        var rootBlock = new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'));
        return (directory, block, writer, rootBlock, [rootBlock]);
    }

    static void SeedExtendDeletedTree(BlockWriter writer)
    {
        writer.TryWriteRow(0, "$MFT", new RowColumns(ParentRow: 0, RowFlags.InUse, Attributes: 0, Size: 0,
            Moment.Ticks, SequenceNumber: 1));
        writer.TryWriteRow(VolumeRootRow, ".", new RowColumns(ParentRow: VolumeRootRow,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
        writer.TryWriteRow(ExtendDirectoryRow, "$Extend", new RowColumns(ParentRow: VolumeRootRow,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
        writer.TryWriteRow(DeletedDirectoryRow, "$Deleted", new RowColumns(ParentRow: ExtendDirectoryRow,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
        writer.TryWriteRow(UserDirectoryRow, "documents", new RowColumns(ParentRow: VolumeRootRow,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
        writer.TryWriteRow(NotesFileRow, "notes.txt", new RowColumns(ParentRow: UserDirectoryRow,
            RowFlags.InUse, Attributes: 32, Size: 99, Moment.Ticks, SequenceNumber: 1));
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(1)]
    public async Task DeletePending_HeldHandleDeleteOnSeededFile_ReportsDeletePendingThenDeleted(int recordsPerBatch)
    {
        var (directory, block, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            UsnJournalEntry[] records =
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.DataExtend | UsnReason.Close, ChangeMoment, sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ];
            var mutator = new JournalMutator(writer);
            var changes = new List<FileChange>();
            for (var start = 0; start < records.Length; start += recordsPerBatch)
            {
                var batch = records.Skip(start).Take(recordsPerBatch).ToArray();
                changes.AddRange(mutator.Apply(rootBlock, batch, journalIdentifier: 7,
                    nextUsn: 1000 + start + batch.Length));
            }

            Assert.DoesNotContain(change => change.Kind == FileChangeKind.Renamed, changes);
            Assert.DoesNotContain(change => change.Kind == FileChangeKind.Modified, changes);
            Assert.AreSequenceEqual(
                [FileChangeKind.DeletePending, FileChangeKind.Deleted],
                [.. changes.Select(change => change.Kind)]);
            Assert.AreSequenceEqual(
                [expectedPath, expectedPath],
                [.. changes.Select(change => change.Path)]);
            Assert.IsNull(changes[0].PreviousPath);
            Assert.IsNull(changes[1].PreviousPath);
            Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, block.Rows[(int)NotesFileRow].Flags);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    [DataRow(7)]
    [DataRow(1)]
    public async Task DeletePending_HeldHandleDeleteAfterCreateCycle_ReportsCreatedDeletePendingDeleted(
        int recordsPerBatch)
    {
        var (directory, _, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            const uint createdFileRow = 15;
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "created.txt");
            UsnJournalEntry[] createRecords =
            [
                CreateEntry(createdFileRow, UserDirectoryRow, "created.txt", UsnReason.FileCreate, ChangeMoment,
                    sequenceNumber: 2),
                CreateEntry(createdFileRow, UserDirectoryRow, "created.txt",
                    UsnReason.DataExtend | UsnReason.FileCreate, ChangeMoment, sequenceNumber: 2),
                CreateEntry(createdFileRow, UserDirectoryRow, "created.txt",
                    UsnReason.DataExtend | UsnReason.FileCreate | UsnReason.Close, ChangeMoment, sequenceNumber: 2)
            ];
            UsnJournalEntry[] deleteRecords =
            [
                CreateEntry(createdFileRow, UserDirectoryRow, "created.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 2),
                CreateEntry(createdFileRow, DeletedDirectoryRow, "0000000000000002", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 2),
                CreateEntry(createdFileRow, DeletedDirectoryRow, "0000000000000002",
                    UsnReason.DataExtend | UsnReason.Close, ChangeMoment, sequenceNumber: 2),
                CreateEntry(createdFileRow, DeletedDirectoryRow, "0000000000000002",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 2)
            ];
            var allRecords = createRecords.Concat(deleteRecords).ToArray();
            var mutator = new JournalMutator(writer);
            var changes = new List<FileChange>();
            for (var start = 0; start < allRecords.Length; start += recordsPerBatch)
            {
                var batch = allRecords.Skip(start).Take(recordsPerBatch).ToArray();
                changes.AddRange(mutator.Apply(rootBlock, batch, journalIdentifier: 7,
                    nextUsn: 1000 + start + batch.Length));
            }

            Assert.DoesNotContain(change => change.Kind == FileChangeKind.Renamed, changes);
            Assert.DoesNotContain(change => change.Kind == FileChangeKind.Modified, changes);
            Assert.AreSequenceEqual(
                [FileChangeKind.Created, FileChangeKind.DeletePending, FileChangeKind.Deleted],
                [.. changes.Select(change => change.Kind)]);
            Assert.AreSequenceEqual(
                [expectedPath, expectedPath, expectedPath],
                [.. changes.Select(change => change.Path)]);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    public async Task DeletePending_HidesRowFromQueriesBetweenEventsAndAllowsSlotReuseAfterDeleted()
    {
        var (directory, block, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            var liveRowCountBefore = block.Header.LiveRowCount;
            var mutator = new JournalMutator(writer);

            var pendingChanges = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001", UsnReason.RenameNewName,
                    ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2000);

            Assert.HasCount(1, pendingChanges);
            Assert.AreEqual(FileChangeKind.DeletePending, pendingChanges[0].Kind);
            Assert.AreEqual(expectedPath, pendingChanges[0].Path);
            Assert.IsNull(pendingChanges[0].PreviousPath);
            Assert.IsTrue(pendingChanges[0].Entry.IsDeleted);
            Assert.IsTrue(block.Rows[(int)NotesFileRow].IsDeleted);
            Assert.AreEqual(RowFlags.InUse | RowFlags.DeletePending, block.Rows[(int)NotesFileRow].Flags);
            Assert.AreEqual(liveRowCountBefore - 1, block.Header.LiveRowCount);

            Assert.IsEmpty(SearchEngine.Search(rootBlock, new SearchQuery("notes.txt"), null, CancellationToken.None));
            Assert.DoesNotContain(entry => entry.Name == "notes.txt",
                SearchEngine.Enumerate(rootBlock, new SearchQuery(null), null, CancellationToken.None));
            Assert.IsNull(LookupEngine.Find(rootBlock, expectedPath, CancellationToken.None));
            Assert.DoesNotContain(entry => entry.Name == "notes.txt",
                IndexNavigation.GetChildren(rootBlock, UserDirectoryRow, CancellationToken.None));

            var holderChanges = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.DataExtend | UsnReason.BasicInfoChange | UsnReason.Close, ChangeMoment,
                    sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2001);
            Assert.IsEmpty(holderChanges);

            var deletedChanges = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2002);

            Assert.HasCount(1, deletedChanges);
            Assert.AreEqual(FileChangeKind.Deleted, deletedChanges[0].Kind);
            Assert.AreEqual(expectedPath, deletedChanges[0].Path);
            Assert.IsNull(deletedChanges[0].PreviousPath);
            Assert.IsTrue(deletedChanges[0].Entry.IsDeleted);
            Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, block.Rows[(int)NotesFileRow].Flags);
            Assert.AreEqual(liveRowCountBefore - 1, block.Header.LiveRowCount);

            var reusedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "reused.txt");
            var reusedChanges = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "reused.txt",
                    UsnReason.FileCreate | UsnReason.Close, ChangeMoment, sequenceNumber: 2)
            ], journalIdentifier: 7, nextUsn: 2003);

            Assert.HasCount(1, reusedChanges);
            Assert.AreEqual(FileChangeKind.Created, reusedChanges[0].Kind);
            Assert.AreEqual(reusedPath, reusedChanges[0].Path);
            Assert.IsFalse(block.Rows[(int)NotesFileRow].IsDeleted);
            Assert.AreEqual(liveRowCountBefore, block.Header.LiveRowCount);
            Assert.ContainsSingle(entry => entry.Name == "reused.txt",
                SearchEngine.Search(rootBlock, new SearchQuery("reused.txt"), null, CancellationToken.None));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    public async Task DeletePending_NegativeControls_OrdinaryAndNonExtendDeletedRenamesReportRenamedAndSecondDeleteIsDropped()
    {
        var (directory, _, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            const uint archiveDirectoryRow = 13;
            const uint userDeletedDirectoryRow = 14;
            const uint nestedExtendDirectoryRow = 15;
            const uint nestedDeletedDirectoryRow = 16;
            const uint secondFileRow = 17;
            const uint thirdFileRow = 18;
            const uint fourthFileRow = 19;

            writer.TryWriteRow(archiveDirectoryRow, "archive", new RowColumns(ParentRow: VolumeRootRow,
                RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
            writer.TryWriteRow(userDeletedDirectoryRow, "$Deleted", new RowColumns(ParentRow: UserDirectoryRow,
                RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
            writer.TryWriteRow(nestedExtendDirectoryRow, "$Extend", new RowColumns(ParentRow: UserDirectoryRow,
                RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
            writer.TryWriteRow(nestedDeletedDirectoryRow, "$Deleted", new RowColumns(ParentRow: nestedExtendDirectoryRow,
                RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));
            writer.TryWriteRow(secondFileRow, "second.txt", new RowColumns(ParentRow: UserDirectoryRow,
                RowFlags.InUse, Attributes: 32, Size: 10, Moment.Ticks, SequenceNumber: 1));
            writer.TryWriteRow(thirdFileRow, "third.txt", new RowColumns(ParentRow: UserDirectoryRow,
                RowFlags.InUse, Attributes: 10, Size: 10, Moment.Ticks, SequenceNumber: 1));
            writer.TryWriteRow(fourthFileRow, "fourth.txt", new RowColumns(ParentRow: UserDirectoryRow,
                RowFlags.InUse, Attributes: 32, Size: 10, Moment.Ticks, SequenceNumber: 1));

            var mutator = new JournalMutator(writer);

            var ordinaryMove = mutator.Apply(rootBlock,
            [
                CreateEntry(secondFileRow, archiveDirectoryRow, "moved.txt",
                    UsnReason.RenameNewName | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2000);
            Assert.HasCount(1, ordinaryMove);
            Assert.AreEqual(FileChangeKind.Renamed, ordinaryMove[0].Kind);

            var nonExtendDeletedMove = mutator.Apply(rootBlock,
            [
                CreateEntry(thirdFileRow, userDeletedDirectoryRow, "0000000000000003",
                    UsnReason.RenameNewName | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2001);
            Assert.HasCount(1, nonExtendDeletedMove);
            Assert.AreEqual(FileChangeKind.Renamed, nonExtendDeletedMove[0].Kind);

            var nonRootExtendDeletedMove = mutator.Apply(rootBlock,
            [
                CreateEntry(fourthFileRow, nestedDeletedDirectoryRow, "0000000000000004",
                    UsnReason.RenameNewName | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2002);
            Assert.HasCount(1, nonRootExtendDeletedMove);
            Assert.AreEqual(FileChangeKind.Renamed, nonRootExtendDeletedMove[0].Kind);

            var realDeletePending = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.RenameNewName, ChangeMoment, sequenceNumber: 1),
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2003);
            Assert.AreSequenceEqual(
                [FileChangeKind.DeletePending, FileChangeKind.Deleted],
                [.. realDeletePending.Select(change => change.Kind)]);

            var secondDeleteOnTombstonedRow = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2004);
            Assert.IsEmpty(secondDeleteOnTombstonedRow);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    public async Task DeletePending_InjectedDetectorSeam_ClassifiesOrdinaryRenameAsDeletePending()
    {
        var (directory, _, writer, rootBlock, snapshot) = CreateExtendDeletedFixture();
        try
        {
            const uint ordinaryStagingRow = 13;
            writer.TryWriteRow(ordinaryStagingRow, "staging", new RowColumns(ParentRow: VolumeRootRow,
                RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 1));

            var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");
            var mutator = FileIndexTestAccess.CreateJournalMutator(writer,
                (_, newParentRecordNumber, newName) =>
                    newParentRecordNumber == ordinaryStagingRow && newName == "staged.bin");

            var changes = mutator.Apply(rootBlock,
            [
                CreateEntry(NotesFileRow, UserDirectoryRow, "notes.txt", UsnReason.RenameOldName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, ordinaryStagingRow, "staged.bin", UsnReason.RenameNewName, ChangeMoment,
                    sequenceNumber: 1),
                CreateEntry(NotesFileRow, ordinaryStagingRow, "staged.bin",
                    UsnReason.DataExtend | UsnReason.Close, ChangeMoment, sequenceNumber: 1),
                CreateEntry(NotesFileRow, ordinaryStagingRow, "staged.bin",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2000);

            Assert.AreSequenceEqual(
                [FileChangeKind.DeletePending, FileChangeKind.Deleted],
                [.. changes.Select(change => change.Kind)]);
            Assert.AreSequenceEqual(
                [expectedPath, expectedPath],
                [.. changes.Select(change => change.Path)]);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
            directory.Dispose();
        }
    }

    [TestMethod]
    public async Task DeletePending_PersistedAcrossBlockReopen_CompletesTombstoneAndReportsDeletedOnLaterFileDelete()
    {
        using var directory = new TempDirectory("mftlib-delete-pending-persist");
        var blockPath = Path.Combine(directory.Path, "T-0BADF00D.mlix");
        var expectedPath = Path.Combine(TestRootDirectory.For('T'), "documents", "notes.txt");

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
                Assert.AreEqual(FileChangeKind.DeletePending, pendingChanges[0].Kind);
                Assert.AreEqual(expectedPath, pendingChanges[0].Path);
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
            Assert.IsTrue(reopenedBlock.Rows[(int)NotesFileRow].IsDeleted);
            Assert.AreEqual(RowFlags.InUse | RowFlags.DeletePending, reopenedBlock.Rows[(int)NotesFileRow].Flags);
            Assert.IsEmpty(SearchEngine.Search(reopenedRootBlock, new SearchQuery("notes.txt"), null,
                CancellationToken.None));

            var reopenedMutator = new JournalMutator(new BlockWriter(reopenedBlock));
            var completedChanges = reopenedMutator.Apply(reopenedRootBlock,
            [
                CreateEntry(NotesFileRow, DeletedDirectoryRow, "0000000000000001",
                    UsnReason.FileDelete | UsnReason.Close, ChangeMoment, sequenceNumber: 1)
            ], journalIdentifier: 7, nextUsn: 2001);

            Assert.HasCount(1, completedChanges);
            Assert.AreEqual(FileChangeKind.Deleted, completedChanges[0].Kind);
            Assert.AreEqual(expectedPath, completedChanges[0].Path);
            Assert.IsNull(completedChanges[0].PreviousPath);
            Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, reopenedBlock.Rows[(int)NotesFileRow].Flags);
        }
        finally
        {
            await reopenedSnapshot.ReleaseNowAsync();
        }
    }
}
