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
    (Dictionary<int, DumpRecord> Records, SafeFileHandle Image) WriteAlignedImage(uint alignment, uint memoryAlignment)
    {
        var records = Files(AlignedImageRecords, 40);
        var path = Path.Combine(_directory.Path, $"aligned-{alignment}-{memoryAlignment}.img");
        FragmentedNtfsImage.Write(path, MftDumpFixture.Build(SalvageRecordSize, AlignedImageRecords, records),
            firstRunRecords: 32, holeClusters: 3);
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
}
