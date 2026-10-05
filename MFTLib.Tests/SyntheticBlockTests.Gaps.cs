using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Free rows, the producer kind, size-unknown marks and the stored scan timestamp: the block
///     editing a consumer needs to seed an index it then opens cache-only.
/// </summary>
public partial class SyntheticBlockTests
{
    static SyntheticRow[] EnumerationRows() =>
    [
        new SyntheticRow(0, "root", 0) { IsDirectory = true, ModifiedUtc = Moment },
        new SyntheticRow(1, "keep.txt", 0) { Size = 10, ModifiedUtc = Moment },
        new SyntheticRow(2, "zebra.txt", 0) { Size = 20, ModifiedUtc = Moment }
    ];

    string SeedEnumerationBlock() =>
        Seed(EnumerationRows(), Options() with { ProducerKind = ProducerKind.Enumeration, RootRow = 0 });

    [TestMethod]
    public async Task WriteCached_AFreeRow_IsSkippedByReadersAndByTheIndex()
    {
        var path = Seed([.. SampleRows(), new SyntheticRow(10, "stale.bin", 6) { IsFree = true, Size = 99 }]);

        Assert.IsFalse(SyntheticBlock.ReadRows(path, Serial).Any(row => row.Name == "stale.bin"));
        Assert.AreEqual(11u, SyntheticBlock.ReadHeader(path, Serial).RowCount, "a free slot below the highest row still counts");
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);

        Assert.AreEqual(0, index.FindByName("stale.bin").Count);
        Assert.AreEqual(5u, index.Drives.Single().LiveRowCount, "the tombstone and the free slot are not live");
    }

    [TestMethod]
    public void Edit_WritingAFreeRowOverALiveOne_FreesTheSlot()
    {
        var path = Seed();

        SyntheticBlock.Edit(path, Serial, editor => editor.WriteRow(editor.ReadRow(7) with { IsFree = true }));

        CollectionAssert.DoesNotContain(SyntheticBlock.ReadRows(path, Serial).Select(row => row.Row).ToArray(), 7u);
    }

    [TestMethod]
    public async Task SetProducerKind_MakesAnEnumerationBlockAcceptJournalMutation()
    {
        var path = SeedEnumerationBlock();
        SyntheticBlock.Edit(path, Serial, editor => editor.SetProducerKind(ProducerKind.Mft));
        Assert.AreEqual(ProducerKind.Mft, SyntheticBlock.ReadHeader(path, Serial).ProducerKind);

        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);
        var zebra = index.FindByName("zebra.txt").Single();
        FileIndexTestAccess.ApplyJournalEntries(index, 'T',
            [SyntheticJournalEntry.Create(new SyntheticJournalEntryOptions
            {
                RecordNumber = zebra.Id.RecordNumber,
                ParentRecordNumber = 0,
                Usn = 1,
                FileName = "zebra.txt",
                Reason = UsnReason.FileDelete | UsnReason.Close
            })], journalId: 0, nextUsn: 0);

        Assert.IsTrue(index.Drives.Single().WatchSupported);
        Assert.IsTrue(zebra.IsDeleted);
        Assert.AreEqual(0, index.FindByName("zebra.txt").Count);
        Assert.AreEqual(1, index.FindByName("keep.txt").Count);
    }

    [TestMethod]
    public void SetProducerKind_AfterTheEditReturned_Throws()
    {
        var path = SeedEnumerationBlock();
        SyntheticBlockEditor? kept = null;
        SyntheticBlock.Edit(path, Serial, editor => kept = editor);

        Assert.ThrowsException<InvalidOperationException>(() => kept!.SetProducerKind(ProducerKind.Mft));
    }

    [TestMethod]
    public async Task MarkSizesUnknown_MarksOnlyTheMatchingRowsInPlace()
    {
        var path = Seed();
        var marked = 0;
        SyntheticBlock.Edit(path, Serial, editor =>
            marked = editor.MarkSizesUnknown(row => row.Name is "report.txt" or "docs" or "gone.tmp"));

        Assert.AreEqual(3, marked);
        var rows = SyntheticBlock.ReadRows(path, Serial).ToDictionary(row => row.Name);
        Assert.IsNull(rows["report.txt"].Size);
        Assert.AreEqual(Moment.AddHours(1), rows["report.txt"].ModifiedUtc, "only the size changes");
        Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Archive, rows["report.txt"].Attributes);
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);
        Assert.IsFalse(index.FindByName("report.txt").Single().SizeKnown);
        Assert.AreEqual(0, index.FindByName("report.txt").Single().Size);
        Assert.IsTrue(index.FindByName("$MFT").Single().SizeKnown);
    }

    [TestMethod]
    public void MarkSizesUnknown_NothingMatches_ReturnsZeroAndRejectsANullFilter()
    {
        var path = Seed();

        SyntheticBlock.Edit(path, Serial, editor =>
        {
            Assert.AreEqual(0, editor.MarkSizesUnknown(row => row.Name == "absent"));
            Assert.ThrowsException<ArgumentNullException>(() => editor.MarkSizesUnknown(null!));
        });
    }

    [TestMethod]
    public void MarkSizeUnknown_ARowAndAGap_MarksTheRowAndRejectsTheGap()
    {
        var path = Seed();

        SyntheticBlock.Edit(path, Serial, editor =>
        {
            editor.MarkSizeUnknown(7);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => editor.MarkSizeUnknown(3));
        });

        Assert.IsNull(SyntheticBlock.ReadRows(path, Serial).Single(row => row.Row == 7).Size);
    }

    [TestMethod]
    public void MarkSizesUnknown_AfterTheEditReturned_Throws()
    {
        var path = Seed();
        SyntheticBlockEditor? kept = null;
        SyntheticBlock.Edit(path, Serial, editor => kept = editor);

        Assert.ThrowsException<InvalidOperationException>(() => kept!.MarkSizesUnknown(_ => true));
        Assert.ThrowsException<InvalidOperationException>(() => kept!.MarkSizeUnknown(7));
    }

    [TestMethod]
    public async Task Complete_WithTheStoredTimestamp_LeavesTheScanTimestampUnchanged()
    {
        var path = Seed();
        var stored = SyntheticBlock.ReadHeader(path, Serial).CompletedUtc;
        SyntheticBlock.Edit(path, Serial, editor =>
        {
            editor.MarkSizeUnknown(7);
            editor.Complete(stored);
        });

        Assert.AreEqual(Moment, SyntheticBlock.ReadHeader(path, Serial).CompletedUtc);
        await using var index = await FileIndex.OpenAsync(CacheOnly(), Token);
        Assert.AreEqual(Moment, index.Drives.Single().ScanTimestamp);
    }

    [DataTestMethod]
    [DataRow(false, false, DisplayName = "cache-tag-edit-mft-foreign")]
    [DataRow(true, false, DisplayName = "cache-tag-edit-enumeration-foreign")]
    [DataRow(false, true, DisplayName = "cache-tag-edit-mft-zero")]
    [DataRow(true, true, DisplayName = "cache-tag-edit-enumeration-zero")]
    public async Task SetCacheTag_PreservesTheBlockAndDeclinesTheOriginalIdentity(
        bool enumeration, bool clearTag)
    {
        var originalTag = new CacheTag("SYNT", 3);
        var replacementTag = clearTag ? default : new CacheTag("TEST", 7);
        var options = Options() with
        {
            CacheTag = originalTag,
            ProducerKind = enumeration ? ProducerKind.Enumeration : ProducerKind.Mft,
            RootRow = enumeration ? 0u : 5u
        };
        var path = Seed(enumeration ? EnumerationRows() : SampleRows(), options);
        var headerBefore = SyntheticBlock.ReadHeader(path, Serial);
        var rowsBefore = SyntheticBlock.ReadRows(path, Serial).ToArray();
        var bytesBefore = await File.ReadAllBytesAsync(path, Token);

        SyntheticBlock.Edit(path, Serial, editor => editor.SetCacheTag(replacementTag));

        Assert.AreEqual(headerBefore with { CacheTag = replacementTag },
            SyntheticBlock.ReadHeader(path, Serial));
        CollectionAssert.AreEqual(rowsBefore, SyntheticBlock.ReadRows(path, Serial).ToArray());
        var bytesAfter = await File.ReadAllBytesAsync(path, Token);
        Assert.AreEqual(bytesBefore.Length, bytesAfter.Length);
        var tagStart = Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.CacheTagFourCc)).ToInt32();
        var tagEnd = Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.CacheTagVersion)).ToInt32()
                     + sizeof(uint);
        CollectionAssert.AreEqual(bytesBefore[..tagStart], bytesAfter[..tagStart],
            "Every header field before the tag must be unchanged.");
        CollectionAssert.AreEqual(bytesBefore[tagEnd..], bytesAfter[tagEnd..],
            "Header padding, all row slots, sequence numbers and the name pool must be unchanged.");

        await using var index = await FileIndex.OpenAsync(
            CacheOnly() with { CacheTag = originalTag }, Token);
        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, drive.State);
        Assert.AreEqual(DriveFailureKind.CacheTagMismatch, drive.FailureKind);
        Assert.AreEqual(BlockSource.None, drive.BlockSource);
    }
}
