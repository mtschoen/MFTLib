using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A row's name identifier changes when it becomes delete-pending (its full path replaces the name) and when it is
///     tombstoned (the bare name returns), so each change must publish a posting like a rename does, and a delta that
///     cannot take the posting refuses the operation before anything is written.
/// </summary>
[TestClass]
public sealed class DeletePendingPostingTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly RowColumns FileColumns = new(0, RowFlags.InUse, 32, 0, Moment.Ticks, 0);

    TempDirectory _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = new TempDirectory("mftlib-pending-posting");

    public void Dispose() => _directory.Dispose();

    (BlockFile Block, BlockWriter Writer, RootBlock[] Snapshot) BuildRoot(uint? deltaCapacity = null)
    {
        // The snapshot owns the block and is released by each test.
        var block = TestBlocks.Create(Path.Combine(_directory.Path, "T-00000001.mlix"), ProducerKind.Mft,
            slotCapacity: 8, namePoolCapacity: 1024, volumeSerial: 1, deltaCapacity: deltaCapacity);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 16, 0, Moment.Ticks, 0));
        writer.TryWriteRow(1, "a.txt", FileColumns);
        writer.Complete(Moment, null);
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'))];
        return (block, writer, snapshot);
    }

    int ExactHits(RootBlock root, string name) => SearchEngine.Search(PinnedRootBlock.Current(root),
        new SearchQuery(name, NameMatchMode.Exact, CaseSensitive: false, IncludeDeleted: true),
        cancellationToken: TestContext.CancellationToken).Count;

    public TestContext TestContext { get; set; } = null!;

    static UsnJournalEntry Entry(ulong record, string name, UsnReason reason, long usn) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = 0,
            Usn = usn,
            TimestampUtc = Moment,
            Reason = reason,
            FileAttributes = FileAttributes.Archive,
            FileName = name
        });

    [TestMethod]
    public async Task TombstoneRestoringANeverInternedSegment_IsReachableByExactName()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(1, @"dir\other.txt"));
            Assert.AreEqual(WriteRefusal.None, writer.MarkTombstone(1));

            Assert.IsNull(PostingIntegrity.Check(block));
            Assert.AreEqual(1, ExactHits(snapshot[0], "other.txt"));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task TombstoneAfterAPostingRebuildWhileDeletePending_IsReachableByTheOriginalName()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(1, @"dir\a.txt"));
            PostingBuilder.Build(block, null, CancellationToken.None);
            Assert.AreEqual(WriteRefusal.None, writer.MarkTombstone(1));

            Assert.IsNull(PostingIntegrity.Check(block));
            Assert.AreEqual(1, ExactHits(snapshot[0], "a.txt"));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ADeltaFullBlock_RefusesBothTransitionsWithDeltaAndChangesNothing()
    {
        var (block, writer, snapshot) = BuildRoot(deltaCapacity: 2);
        try
        {
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(3, "fills-one.txt", FileColumns));

            // Refused delete-pending: the block holds one free delta entry, so use it, then refuse the next.
            Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(1, @"dir\fresh.txt"));
            var flags = writer.ReadRowFlags(1);
            var name = NamePool.ReadRowName(block, 1).ToString();
            var entries = block.NameEntryCount;
            var pool = block.NamePoolUsed;

            Assert.AreEqual(WriteRefusal.Delta, writer.MarkTombstone(1));
            AssertUnchanged(block, writer, 1, flags, name, entries, pool);

            Assert.AreEqual(WriteRefusal.Delta, writer.MarkDeletePending(3, @"dir\another.txt"));
            Assert.AreEqual(RowFlags.InUse, writer.ReadRowFlags(3));
            Assert.AreEqual("fills-one.txt", NamePool.ReadRowName(block, 3).ToString());
            Assert.AreEqual(entries, block.NameEntryCount);
            Assert.AreEqual(pool, block.NamePoolUsed);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    static void AssertUnchanged(BlockFile block, BlockWriter writer, uint row, RowFlags flags, string name,
        uint entries, uint pool)
    {
        Assert.AreEqual(flags, writer.ReadRowFlags(row));
        Assert.AreEqual(name, NamePool.ReadRowName(block, row).ToString());
        Assert.AreEqual(entries, block.NameEntryCount);
        Assert.AreEqual(pool, block.NamePoolUsed);
    }

    [TestMethod]
    public async Task ADeletePendingRenameRefusedByTheDelta_StopsCleanlyAtItsRecord()
    {
        var (block, writer, snapshot) = BuildRoot(deltaCapacity: 1);
        try
        {
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(3, "fills-the-delta.txt", FileColumns));
            var entries = block.NameEntryCount;
            var pool = block.NamePoolUsed;
            var mutator = new JournalMutator(writer, (_, _, _) => true);

            var result = mutator.ApplyUntilRefusal(snapshot[0],
                [Entry(1, "$deleted-name", UsnReason.RenameNewName, usn: 700)], 0, journalIdentifier: 9, nextUsn: 900);

            Assert.AreEqual(WriteRefusal.Delta, result.Refusal!.Value.Reason);
            Assert.IsTrue(result.Refusal!.Value.Growable);
            Assert.AreEqual(700L, block.Header.UsnNextUsn);
            Assert.AreEqual(RowFlags.InUse, writer.ReadRowFlags(1));
            Assert.AreEqual("a.txt", NamePool.ReadRowName(block, 1).ToString());
            Assert.AreEqual(entries, block.NameEntryCount);
            Assert.AreEqual(pool, block.NamePoolUsed);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ADeleteRefusedByTheDelta_StopsCleanlyAtItsRecord()
    {
        var (block, writer, snapshot) = BuildRoot(deltaCapacity: 2);
        try
        {
            Assert.AreEqual(WriteRefusal.None, writer.MarkDeletePending(1, @"dir\fresh.txt"));
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(3, "fills-the-delta.txt", FileColumns));
            var flags = writer.ReadRowFlags(1);
            var name = NamePool.ReadRowName(block, 1).ToString();
            var entries = block.NameEntryCount;
            var pool = block.NamePoolUsed;

            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Entry(1, "$deleted-name", UsnReason.FileDelete | UsnReason.Close, usn: 800)], 0, journalIdentifier: 9,
                nextUsn: 900);

            Assert.AreEqual(WriteRefusal.Delta, result.Refusal!.Value.Reason);
            Assert.AreEqual(800L, block.Header.UsnNextUsn);
            Assert.IsEmpty(result.Changes);
            AssertUnchanged(block, writer, 1, flags, name, entries, pool);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
