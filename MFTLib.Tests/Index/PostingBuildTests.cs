using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class PostingBuildTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    static readonly RowColumns RootColumns = new(0, RowFlags.InUse | RowFlags.Directory, 16, 0, Moment.Ticks, 0);

    TempDirectory _directory = null!;
    string _blockPath = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = new TempDirectory("mftlib-postings");
        _blockPath = Path.Combine(_directory.Path, "T-0BADF00D.mlix");
    }

    public void Dispose() => _directory.Dispose();

    static RowColumns FileColumns(RowFlags flags = RowFlags.InUse) => new(0, flags, 0, 0, Moment.Ticks, 0);

    static uint[] BaseList(BlockFile block, uint nameId)
    {
        var offsets = block.PostingOffsets;
        return block.PostingRows.Slice((int)offsets[(int)nameId], (int)(offsets[(int)nameId + 1] - offsets[(int)nameId]))
            .ToArray();
    }

    static uint NameIdOf(BlockFile block, uint row) => FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in block.Rows[(int)row]));

    [TestMethod]
    public void Complete_OnAnEmptyBlock_BuildsAnEmptyBase()
    {
        using var block = TestBlocks.Create(_blockPath);

        new BlockWriter(block).Complete(Moment, null);

        Assert.IsTrue(block.Header.IsComplete);
        Assert.IsTrue(block.PostingsBuilt);
        Assert.AreEqual(0u, block.PostingBaseNameCount);
        Assert.AreEqual(0u, block.PostingBaseRowCount);
        Assert.AreEqual(0u, block.PostingOffsets[1]);
        Assert.IsNull(PostingIntegrity.Check(block));
    }

    [TestMethod]
    public void Complete_GroupsRowsByNameAscendingAndIncludesFreedAndTombstonedRows()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "", RootColumns));
        Assert.IsTrue(writer.TryWriteRow(1, "a.txt", FileColumns()));
        Assert.IsTrue(writer.TryWriteRow(2, "b.txt", FileColumns()));
        Assert.IsTrue(writer.TryWriteRow(3, "a.txt", FileColumns()));
        Assert.IsTrue(writer.TryWriteRow(4, "b.txt", FileColumns(RowFlags.InUse | RowFlags.Tombstone)));
        Assert.IsTrue(writer.TryWriteRow(5, "a.txt", FileColumns(RowFlags.None)));
        // Rows 6 to 8 are a gap with no descriptor at all.
        Assert.IsTrue(writer.TryWriteRow(9, "c.txt", FileColumns()));

        writer.Complete(Moment, null);

        var a = NameIdOf(block, 1);
        var b = NameIdOf(block, 2);
        var c = NameIdOf(block, 9);
        Assert.AreEqual(0u, NameIdOf(block, 0), "the unnamed root holds name zero");
        CollectionAssert.AreEqual(new uint[] { 1, 3, 5 }, BaseList(block, a));
        CollectionAssert.AreEqual(new uint[] { 2, 4 }, BaseList(block, b));
        CollectionAssert.AreEqual(new uint[] { 9 }, BaseList(block, c));
        Assert.AreEqual(block.NameEntryCount, block.PostingBaseNameCount);
        Assert.AreEqual(6u, block.PostingBaseRowCount);
        Assert.AreEqual(6u, block.PostingOffsets[(int)block.PostingBaseNameCount + 1]);
        Assert.AreEqual(0u, block.DeltaEntryCount);
        Assert.IsNull(PostingIntegrity.Check(block));
    }

    [TestMethod]
    public void Complete_ANameSharedByOneHundredThousandRows_ListsEveryRowAscending()
    {
        const uint rows = 100_000;
        using var block = TestBlocks.Create(_blockPath, slotCapacity: rows + 10, namePoolCapacity: 4096);
        var writer = new BlockWriter(block);
        for (uint row = 0; row < rows; row++)
        {
            Assert.IsTrue(writer.TryWriteRow(row, row % 10 == 0 ? "other" : "index.js", FileColumns()));
        }

        writer.Complete(Moment, null);

        var shared = NameIdOf(block, 1);
        var list = BaseList(block, shared);
        Assert.HasCount(90_000, list);
        Assert.IsTrue(list.Zip(list.Skip(1), (left, right) => left < right).All(ascending => ascending));
        Assert.AreEqual(rows, block.PostingBaseRowCount);
        Assert.IsNull(PostingIntegrity.Check(block));
    }

    [TestMethod]
    public void Complete_AgainAfterAnEdit_RebuildsTheBaseAndRestartsTheDeltaChain()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "", RootColumns));
        Assert.IsTrue(writer.TryWriteRow(1, "old", FileColumns()));
        writer.Complete(Moment, null);
        Assert.IsTrue(writer.TryRenameRow(1, "new", 0));
        block.DeltaEntries[1] = new DeltaEntry { Row = 1, NameId = NameIdOf(block, 1), Next = 0 };
        block.DeltaHeads[(int)(NameIdOf(block, 1) & (block.BucketCount - 1))] = 1;
        block.PublishDeltaEntryCount(1);

        writer.Complete(Moment, null);

        Assert.AreEqual(0u, block.Header.DeltaEntryCount);
        Assert.AreEqual(0u, block.DeltaEntryCount);
        Assert.IsTrue(block.DeltaHeads.ToArray().All(head => head == 0));
        CollectionAssert.AreEqual(new uint[] { 1 }, BaseList(block, NameIdOf(block, 1)));
        Assert.IsNull(PostingIntegrity.Check(block));
    }

    [TestMethod]
    public void Complete_CancelledBeforeTheBuild_LeavesNoCompleteFlag()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "", RootColumns));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => writer.Complete(Moment, null, cancellationToken: cancelled.Token));

        Assert.IsFalse(block.Header.IsComplete);
        Assert.IsFalse(block.PostingsBuilt);
    }

    [TestMethod]
    public void Complete_CancelledBetweenThePasses_LeavesNoCompleteFlagAndALaterCompleteSucceeds()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "", RootColumns));
        Assert.IsTrue(writer.TryWriteRow(1, "a.txt", FileColumns()));
        using var canceller = new SecondReportCanceller();

        Assert.ThrowsExactly<OperationCanceledException>(() => writer.Complete(Moment, null, canceller.Report,
            canceller.Token));

        Assert.AreEqual(2, canceller.Reports, "the cancelling report is the start of the placing pass");
        Assert.IsFalse(block.Header.IsComplete);

        writer.Complete(Moment, null);
        Assert.IsTrue(block.Header.IsComplete);
        Assert.IsNull(PostingIntegrity.Check(block));
    }

    [TestMethod]
    public void Complete_ReportsProgressAtTheStartOfEachPass()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "", RootColumns));
        var reports = 0;

        writer.Complete(Moment, null, () => reports++);

        Assert.AreEqual(2, reports);
    }

    [TestMethod]
    public void Complete_ARowNamingAnEntryPastThePublishedCount_ThrowsAndLeavesNoCompleteFlag()
    {
        using var block = TestBlocks.Create(_blockPath);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "a", FileColumns()));
        FileRow.WriteDescriptorWord(ref block.Rows[0], block.NameEntryCount + 1, RowFlags.InUse);

        Assert.ThrowsExactly<InvalidDataException>(() => writer.Complete(Moment, null));

        Assert.IsFalse(block.Header.IsComplete);
    }

    [TestMethod]
    public void Integrity_ARowMissingFromTheBase_IsReported()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "a", FileColumns()));
        Assert.IsTrue(writer.TryWriteRow(1, "a", FileColumns()));
        writer.Complete(Moment, null);
        // Rename row 1 behind the posting structures' back: its new name has no posting.
        Assert.IsTrue(writer.TryRenameRow(1, "b", 0));

        var failure = PostingIntegrity.Check(block);

        Assert.IsNotNull(failure);
        StringAssert.Contains(failure, "row 1");
    }

    [TestMethod]
    public void Integrity_ABaseListOutOfOrder_IsReported()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "a", FileColumns()));
        Assert.IsTrue(writer.TryWriteRow(1, "a", FileColumns()));
        writer.Complete(Moment, null);
        (block.PostingRows[0], block.PostingRows[1]) = (block.PostingRows[1], block.PostingRows[0]);

        StringAssert.Contains(PostingIntegrity.Check(block), "ascending");
    }

    [TestMethod]
    public void Integrity_ADeltaEntryInTheWrongHead_IsReported()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "a", FileColumns()));
        writer.Complete(Moment, null);
        var nameId = NameIdOf(block, 0);
        block.DeltaEntries[1] = new DeltaEntry { Row = 0, NameId = nameId, Next = 0 };
        block.DeltaHeads[(int)((nameId + 1) & (block.BucketCount - 1))] = 1;
        block.PublishDeltaEntryCount(1);

        StringAssert.Contains(PostingIntegrity.Check(block), "wrong head");
    }

    [TestMethod]
    public void Integrity_AnUnreachableDeltaEntry_IsReported()
    {
        using var block = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, "a", FileColumns()));
        writer.Complete(Moment, null);
        block.PublishDeltaEntryCount(1);

        StringAssert.Contains(PostingIntegrity.Check(block), "unreachable");
    }

    [TestMethod]
    public void Open_OfACompleteBlock_ReportsPostingsBuilt()
    {
        using (var block = TestBlocks.Create(_blockPath, deleteOnClose: false))
        {
            Assert.IsTrue(new BlockWriter(block).TryWriteRow(0, "a", FileColumns()));
            Assert.IsFalse(block.PostingsBuilt);
            new BlockWriter(block).Complete(Moment, null);
        }

        using var reopened = BlockFile.Open(_blockPath, 0x0BADF00D, out var validation);

        Assert.IsNull(validation);
        Assert.IsNotNull(reopened);
        Assert.IsTrue(reopened.PostingsBuilt);
        Assert.AreEqual(1u, reopened.PostingBaseNameCount);
        Assert.AreEqual(1u, reopened.PostingBaseRowCount);
        Assert.IsNull(PostingIntegrity.Check(reopened));
    }

    [TestMethod]
    public void TryAdoptPublishedCursors_AdoptsThePostingCursorsOfACompletedHeader()
    {
        using var target = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        using var source = TestBlocks.Create(Path.Combine(_directory.Path, "source.mlix"), slotCapacity: 64,
            namePoolCapacity: 4096);
        var writer = new BlockWriter(source);
        Assert.IsTrue(writer.TryWriteRow(0, "a", FileColumns()));
        writer.Complete(Moment, null);

        Assert.IsTrue(target.TryAdoptPublishedCursors(source.Header));

        Assert.IsTrue(target.PostingsBuilt);
        Assert.AreEqual(1u, target.PostingBaseNameCount);
        Assert.AreEqual(1u, target.PostingBaseRowCount);
    }

    [TestMethod]
    public void TryAdoptPublishedCursors_AnIncompleteHeader_DoesNotMarkThePostingsBuilt()
    {
        using var target = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var published = target.Header;

        Assert.IsTrue(target.TryAdoptPublishedCursors(published));

        Assert.IsFalse(target.PostingsBuilt);
    }

    [TestMethod]
    [DataRow("DeltaCapacity")]
    [DataRow("DeltaEntryCount")]
    [DataRow("PostingBaseNameCount")]
    [DataRow("PostingBaseRowCount")]
    public void TryAdoptPublishedCursors_APostingFieldBeyondItsBound_IsRefused(string field)
    {
        using var target = TestBlocks.Create(_blockPath, slotCapacity: 64, namePoolCapacity: 4096);
        var published = target.Header;
        switch (field)
        {
            case "DeltaCapacity": published.DeltaCapacity++; break;
            case "DeltaEntryCount": published.DeltaEntryCount = published.DeltaCapacity + 1; break;
            case "PostingBaseNameCount": published.PostingBaseNameCount = published.NameEntryCount + 1; break;
            default: published.PostingBaseRowCount = published.RowCount + 1; break;
        }

        Assert.IsFalse(target.TryAdoptPublishedCursors(published));
    }

    [TestMethod]
    public void WriteToBlock_PublishesThePostingBuildStepBeforeTheFlush()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft, slotCapacity: 128, namePoolCapacity: 16 * 1024,
            rootRow: 5);
        var reporter = new RecordingReporter();

        MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), [],
            new MftScanOptions(), new BlockWriteReporting(null, reporter), TestContext.CancellationToken);

        Assert.IsTrue(reporter.Steps.Count >= 2);
        Assert.AreEqual(MftBlockScan.PostingBuildStep, reporter.Steps[0]);
        Assert.AreEqual(MftBlockScan.FlushStep, reporter.Steps[^1]);
    }

    [TestMethod]
    public void WriteToBlock_ACancelledToken_LeavesTheBlockIncomplete()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft, slotCapacity: 128, namePoolCapacity: 16 * 1024,
            rootRow: 5);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => MftBlockScan.WriteToBlock(block,
            new BlockStamp(default, TimeProvider.System), [], new MftScanOptions(), new BlockWriteReporting(null, null),
            cancelled.Token));

        Assert.IsFalse(block.Header.IsComplete);
    }

    sealed class SecondReportCanceller : IDisposable
    {
        readonly CancellationTokenSource _source = new();

        public int Reports { get; private set; }

        public CancellationToken Token => _source.Token;

        public void Report()
        {
            if (++Reports == 2)
            {
                _source.Cancel();
            }
        }

        public void Dispose() => _source.Dispose();
    }

    sealed class RecordingReporter : ISourceOperationReporter
    {
        public List<string> Steps { get; } = [];

        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName) => Steps.Add(stepName);
    }
}
