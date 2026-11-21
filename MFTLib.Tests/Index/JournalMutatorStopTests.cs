using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="JournalMutator.ApplyUntilRefusal" /> stops at the first record the block cannot store and leaves the
///     block exactly consistent at that record; the index grows the block or skips the record from there.
/// </summary>
[TestClass]
public sealed class JournalMutatorStopTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    TempDirectory _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = new TempDirectory("mftlib-mutator-stop");

    public void Dispose() => _directory.Dispose();

    (BlockFile Block, BlockWriter Writer, RootBlock[] Snapshot) BuildRoot(uint slotCapacity = 8,
        uint namePoolCapacity = 512, uint? nameEntryCapacity = null, uint? deltaCapacity = null)
    {
        var block = TestBlocks.Create(Path.Combine(_directory.Path, "T-0000000000000001.mlix"), ProducerKind.Mft,
            slotCapacity, namePoolCapacity, nameEntryCapacity, volumeSerial: 1, deltaCapacity: deltaCapacity);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", new RowColumns(ParentRow: 0, RowFlags.InUse | RowFlags.Directory,
            Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 0));
        writer.Complete(Moment, null);
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'))];
        return (block, writer, snapshot);
    }

    static UsnJournalEntry Create(ulong recordNumber, string fileName, long usn, ulong parent = 0,
        UsnReason reason = UsnReason.FileCreate | UsnReason.Close)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parent,
            Usn = usn,
            TimestampUtc = Moment,
            Reason = reason,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    [TestMethod]
    public async Task StopsAtTheFirstRefusedRecord_WithThePrefixAppliedAndTheCursorOnTheRefusedUsn()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            var generation = block.Header.Generation;
            UsnJournalEntry[] entries = [Create(1, "fits.txt", usn: 100), Create(400, "past-the-slots.txt", usn: 200),
                Create(2, "never-reached.txt", usn: 300)];

            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0], entries, 0, journalIdentifier: 9,
                nextUsn: 400);

            Assert.AreEqual(new JournalRefusal { Index = 1, Reason = WriteRefusal.Slots, Growable = true }, result.Refusal);
            Assert.HasCount(1, result.Changes);
            Assert.AreEqual("fits.txt", result.Changes[0].Entry.Name);
            Assert.AreEqual(9ul, block.Header.UsnJournalIdentifier);
            Assert.AreEqual(200L, block.Header.UsnNextUsn);
            Assert.AreEqual(generation + 1, block.Header.Generation);
            Assert.IsTrue(block.Rows[1].IsInUse);
            Assert.IsFalse(block.Rows[2].IsInUse);
            Assert.IsFalse(block.Header.IsCapacityExhausted);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ARefusalAtTheFirstRecord_ChangesNothingButTheCursor()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            var generation = block.Header.Generation;

            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Create(400, "past-the-slots.txt", usn: 200)], 0, journalIdentifier: 9, nextUsn: 400);

            Assert.IsEmpty(result.Changes);
            Assert.AreEqual(0, result.Refusal!.Value.Index);
            Assert.AreEqual(200L, block.Header.UsnNextUsn);
            Assert.AreEqual(generation, block.Header.Generation);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task AWholeBatchThatFits_StoresTheBatchCursorAndReportsNoRefusal()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Create(1, "one.txt", usn: 100), Create(2, "two.txt", usn: 200)], 0, journalIdentifier: 9, nextUsn: 400);

            Assert.IsNull(result.Refusal);
            Assert.HasCount(2, result.Changes);
            Assert.AreEqual(400L, block.Header.UsnNextUsn);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ResumingAtTheEntryCount_StoresTheBatchCursorWithoutBumpingTheGeneration()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            var generation = block.Header.Generation;

            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Create(1, "one.txt", usn: 100)], firstIndex: 1, journalIdentifier: 9, nextUsn: 400);

            Assert.IsNull(result.Refusal);
            Assert.IsEmpty(result.Changes);
            Assert.AreEqual(400L, block.Header.UsnNextUsn);
            Assert.AreEqual(generation, block.Header.Generation);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task EveryRefusalReason_StopsWithItsReasonAndWritesNothing()
    {
        // Each case exhausts one region: name entries, name pool, or the posting delta.
        var cases = new (string Description, Func<(BlockFile, BlockWriter, RootBlock[])> Build, WriteRefusal Reason)[]
        {
            ("entries", () => BuildRoot(nameEntryCapacity: 1), WriteRefusal.NameEntries),
            ("pool", () => BuildRoot(namePoolCapacity: 1), WriteRefusal.NamePool),
            ("delta", () => BuildRoot(deltaCapacity: 1), WriteRefusal.Delta)
        };
        foreach (var (description, build, reason) in cases)
        {
            var (block, writer, snapshot) = build();
            try
            {
                if (reason != WriteRefusal.NamePool)
                {
                    Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(3, "uses-the-only-slot-of-the-region", new RowColumns(0,
                        RowFlags.InUse, 0, 0, 0, 0)));
                }

                var entries = block.NameEntryCount;
                var pool = block.NamePoolUsed;
                var delta = block.DeltaEntryCount;
                var rows = block.RowCount;

                var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                    [Create(1, "refused-everywhere.txt", usn: 100)], 0, journalIdentifier: 9, nextUsn: 400);

                Assert.AreEqual(new JournalRefusal { Index = 0, Reason = reason, Growable = true }, result.Refusal, description);
                Assert.AreEqual(entries, block.NameEntryCount, description);
                Assert.AreEqual(pool, block.NamePoolUsed, description);
                Assert.AreEqual(delta, block.DeltaEntryCount, description);
                Assert.AreEqual(rows, block.RowCount, description);
                Assert.IsFalse(block.Header.IsCapacityExhausted, description);
            }
            finally
            {
                await snapshot.ReleaseNowAsync();
            }
        }
    }

    [TestMethod]
    public async Task ARecordNumberAbove32Bits_IsRefusedAsNotGrowable()
    {
        var (_, writer, snapshot) = BuildRoot();
        try
        {
            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Create(uint.MaxValue + 1ul, "huge.txt", usn: 100)], 0, journalIdentifier: 9, nextUsn: 400);

            Assert.AreEqual(new JournalRefusal { Index = 0, Reason = WriteRefusal.Slots, Growable = false }, result.Refusal);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task AParentAbove32Bits_IsRefusedAsNotGrowable()
    {
        var (_, writer, snapshot) = BuildRoot();
        try
        {
            // A modification of a row the index never had must hydrate it, which needs a 32-bit parent.
            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Create(3, "orphan.txt", usn: 100, parent: uint.MaxValue + 1ul, reason: UsnReason.DataExtend)], 0,
                journalIdentifier: 9, nextUsn: 400);

            Assert.AreEqual(new JournalRefusal { Index = 0, Reason = WriteRefusal.Slots, Growable = false }, result.Refusal);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ACreateWithAParentAbove32Bits_IsRefusedWholeAndNotGrowable()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            var entries = block.NameEntryCount;

            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Create(3, "truncated-parent.txt", usn: 100, parent: uint.MaxValue + 1ul)], 0, journalIdentifier: 9,
                nextUsn: 400);

            Assert.AreEqual(new JournalRefusal { Index = 0, Reason = WriteRefusal.Slots, Growable = false }, result.Refusal);
            Assert.IsFalse(block.Rows[3].IsInUse);
            Assert.AreEqual(entries, block.NameEntryCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ARenameWithAParentAbove32Bits_IsRefusedWholeAndNotGrowable()
    {
        var (block, writer, snapshot) = BuildRoot();
        try
        {
            writer.TryWriteRow(3, "stays.txt", new RowColumns(0, RowFlags.InUse, 32, 0, Moment.Ticks, 0));
            var entries = block.NameEntryCount;

            var result = new JournalMutator(writer).ApplyUntilRefusal(snapshot[0],
                [Create(3, "moved.txt", usn: 100, parent: uint.MaxValue + 1ul, reason: UsnReason.RenameNewName)], 0,
                journalIdentifier: 9, nextUsn: 400);

            Assert.AreEqual(new JournalRefusal { Index = 0, Reason = WriteRefusal.Slots, Growable = false }, result.Refusal);
            Assert.AreEqual("stays.txt", NamePool.ReadRowName(block, 3).ToString());
            Assert.AreEqual(0u, block.Rows[3].ParentRow);
            Assert.AreEqual(entries, block.NameEntryCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
