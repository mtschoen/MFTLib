using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

// Inline reads of a volume image under SetVolumeAlignmentOverride, which fails every volume read whose destination,
// offset or length is not a multiple of the armed alignment. The image has 1024-byte records in 4096-byte clusters, so
// 512 is an alignment the records keep and 4096 one they do not.
public sealed partial class InlineContentReaderStateTests
{
    const int AlignedImageRecords = 64;

    // Records 20 to 22 share one read, as do 40 and 41 in the image's second run.
    static readonly long[] AlignedReadRecords = [20, 21, 22, 40, 41];

    // The caller disposes the image after the reader, which borrows its handle.
    // A caller that wants the volume to end early names its length in bytes.
    (Dictionary<int, DumpRecord> Records, SafeFileHandle Image) WriteAlignedImage(uint alignment, uint memoryAlignment,
        long? imageLength = null)
    {
        var records = Files(AlignedImageRecords, 40);
        var path = Path.Combine(_directory.Path, $"aligned-{alignment}-{memoryAlignment}.img");
        FragmentedNtfsImage.Write(path, MftDumpFixture.Build(SalvageRecordSize, AlignedImageRecords, records),
            firstRunRecords: 32, holeClusters: 3);
        if (imageLength is { } length)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            stream.SetLength(length);
        }

        NativeTestHooks.NativeSetVolumeRecordSizeOverride(SalvageRecordSize);
        NativeTestHooks.NativeSetVolumeAlignmentOverride(alignment, memoryAlignment);
        return (records, File.OpenHandle(path));
    }

    static void AssertEachReturned(Dictionary<int, DumpRecord> records, InlineAnswer[] answers)
    {
        foreach (var (number, answer) in AlignedReadRecords.Zip(answers))
        {
            Assert.AreEqual(InlineContentOutcome.Returned, answer.Outcome, $"record {number}");
            CollectionAssert.AreEqual(records[(int)number].Content, answer.Content, $"record {number}");
        }
    }

    // At 512 every group read lands in the scratch buffer directly; at 4096 a group that is not whole sectors goes
    // through the bounce buffer and returns the same bytes.
    [TestMethod]
    [DataRow(512U, true)]
    [DataRow(4096U, false)]
    public void Read_GroupsUnderAnEnforcedAlignment_ReturnEachRecordsBytes(uint alignment, bool direct)
    {
        var (records, image) = WriteAlignedImage(alignment, alignment);
        using (image)
        using (var reader = NativeInlineContentReader.Open(image))
        {
            var bouncedAtOpen = NativeTestHooks.NativeGetBouncedVolumeReads();

            var answers = ReadOpen(reader, AlignedReadRecords);

            AssertEachReturned(records, answers);
            Assert.AreEqual(2, reader.NativeReadCalls, "one read per group");
            Assert.AreEqual(direct, NativeTestHooks.NativeGetBouncedVolumeReads() == bouncedAtOpen);
        }
    }

    // The failed group read leaves each record to a read of its own into its place in the scratch buffer: at 512 that
    // place is aligned and the reread is direct; at 4096 it is not, and the reread goes through the bounce buffer.
    [TestMethod]
    [DataRow(512U, true)]
    [DataRow(4096U, false)]
    public void Read_RecordRereadsAfterAFailedGroupRead_ReturnEachRecordsBytes(uint alignment, bool direct)
    {
        var (records, image) = WriteAlignedImage(alignment, alignment);
        using (image)
        using (var reader = NativeInlineContentReader.Open(image))
        {
            var bouncedAtOpen = NativeTestHooks.NativeGetBouncedVolumeReads();
            NativeTestHooks.NativeSetReadFailMask(0b1);

            var answers = ReadOpen(reader, AlignedReadRecords);

            AssertEachReturned(records, answers);
            Assert.AreEqual(5, reader.NativeReadCalls, "the failed group read, three rereads, then the second group");
            Assert.AreEqual(direct, NativeTestHooks.NativeGetBouncedVolumeReads() == bouncedAtOpen);
        }
    }

    // A volume of 512-byte logical and 4096-byte physical sectors: offsets and lengths keep 512, addresses need 4096.
    // The failed group read leaves each record to a read of its own at its place in the scratch buffer, so a record
    // that starts 1024 or 2048 bytes in has a compliant offset and length and an address that is not page-aligned:
    // exactly those two rereads bounce, for their address alone, and every record returns its exact bytes.
    [TestMethod]
    public void Read_RecordRereadsWithPhysicalSectorsLargerThanLogical_BounceOnlyForTheAddress()
    {
        var (records, image) = WriteAlignedImage(512, 4096);
        using (image)
        using (var reader = NativeInlineContentReader.Open(image))
        {
            var bouncedAtOpen = NativeTestHooks.NativeGetBouncedVolumeReads();
            NativeTestHooks.NativeSetReadFailMask(0b1);

            var answers = ReadOpen(reader, AlignedReadRecords);

            AssertEachReturned(records, answers);
            Assert.AreEqual(5, reader.NativeReadCalls, "the failed group read, three rereads, then the second group");
            Assert.AreEqual(2UL, NativeTestHooks.NativeGetBouncedVolumeReads() - bouncedAtOpen);
        }
    }

    // The image ends halfway through record 22's slot, 2560 bytes into the last 4096-byte sector the bounce buffer
    // reads. The group read of records 21 to 23 starts 1024 bytes into that sector, so taking that prefix from the
    // bytes that arrived leaves 1536: record 21 whole and half of 22, which cuts off 22 and 23. The rereads then gain
    // 512 bytes for record 22 and none for record 23, whose offset lies past the end of the file.
    [TestMethod]
    public void Read_ShortBounceReadAtTheEndOfTheVolume_ReturnsTheWholeRecordAndNoMore()
    {
        const long firstRunStart = FragmentedNtfsImage.ClusterSize;
        const long imageLength = firstRunStart + (22 * SalvageRecordSize) + (SalvageRecordSize / 2);
        var (records, image) = WriteAlignedImage(4096, 4096, imageLength);
        using (image)
        using (var reader = NativeInlineContentReader.Open(image))
        {
            var answers = ReadOpen(reader, 21, 22, 23);

            Assert.AreEqual(InlineContentOutcome.Returned, answers[0].Outcome, "record 21");
            CollectionAssert.AreEqual(records[21].Content, answers[0].Content, "record 21");
            Assert.AreEqual(InlineContentOutcome.Unreadable, answers[1].Outcome, "record 22 ends past the volume");
            Assert.AreEqual(InlineContentOutcome.Unreadable, answers[2].Outcome, "record 23 starts past the volume");
            Assert.AreEqual(3, reader.NativeReadCalls, "the group read, then a read of each cut record");
            Assert.AreEqual(1536L + 512 + 0, reader.NativeBytesRead);
        }
    }
}
