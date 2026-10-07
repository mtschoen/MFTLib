using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A live volume scan passes over an allocated record whose fixup is invalid, and that record
///     does not vanish: the parse counts it, and the shared block scan adds the count to the
///     skipped records the drive reports.
/// </summary>
[TestClass]
[DoNotParallelize]
public class LiveScanInvalidFixupTests
{
    const int ClusterSize = 4096;
    const int RecordSize = 1024;

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        Kernel32.ResetToDefaults();
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    [TestMethod]
    public void VolumeParse_AllocatedRecordWithAnInvalidFixup_IsCountedAndItsNeighboursAreReturned()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var (imagePath, corruptRecord) = WriteImageWithOneCorruptFixup();
        using var image = File.OpenHandle(imagePath);

        var resultPointer = MFTLibNative._parseMftRecordsWithProgress(image, false, 64, IntPtr.Zero,
            null);
        var counted = Marshal.PtrToStructure<MftParseResult>(resultPointer);
        using var result = new MftResult(resultPointer);
        var recordNumbers = result.ToArray().Select(record => record.RecordNumber).ToArray();

        Assert.AreEqual(string.Empty, counted.ErrorMessage);
        Assert.AreEqual(0u, counted.InvalidInput, "a live scan never fails on the record");
        Assert.AreEqual(1UL, counted.InvalidFixupRecords);
        Assert.AreEqual(1UL, result.InvalidFixupRecordCount);
        CollectionAssert.DoesNotContain(recordNumbers, corruptRecord);
        CollectionAssert.Contains(recordNumbers, corruptRecord - 1);
        CollectionAssert.Contains(recordNumbers, corruptRecord + 1);
    }

    [TestMethod]
    public void LiveBlockScan_AllocatedRecordWithAnInvalidFixup_IsAddedToTheSkippedRecordCount()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var (imagePath, corruptRecord) = WriteImageWithOneCorruptFixup();
        ServeImageAsVolume(imagePath);
        using var block = CreateBlock();

        var batches = LiveVolumeSources.ScanDriveRecordBatches("C", new ParseThreadAllowance(2), new QuietReporter(),
            null, default, CancellationToken.None);
        var written = MftBlockScan.WriteToBlock(block, new BlockStamp(default, () => DateTime.UtcNow), batches,
            MftBlockRowFilter.Full, new BlockWriteReporting(null, null), CancellationToken.None);

        Assert.AreEqual(1L, written.SkippedRecordCount);
        Assert.IsFalse(block.Rows[(int)corruptRecord].IsInUse, "the corrupt record has no row");
        Assert.IsTrue(block.Rows[(int)corruptRecord - 1].IsInUse, "the record before it is imported");
        Assert.IsTrue(block.Rows[(int)corruptRecord + 1].IsInUse, "the record after it is imported");
    }

    [TestMethod]
    public async Task FromLocalVolumes_AllocatedRecordWithAnInvalidFixup_IsReportedAsASkippedRecord()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var (imagePath, corruptRecord) = WriteImageWithOneCorruptFixup();
        ServeImageAsVolume(imagePath);
        var request = new MftBlockProduceRequest
        {
            DriveLetter = 'C',
            VolumeSerial = 0,
            BlockPath = Path.Combine(_directory, "local.mlix"),
            DeleteOnClose = true
        };

        var produced = await MftIndexSources.FromLocalVolumes().Producer(request, CancellationToken.None);

        using var block = produced.Block;
        Assert.AreEqual(1, produced.SkippedRecordCount);
        Assert.IsFalse(block.Rows[(int)corruptRecord].IsInUse, "the corrupt record has no row");
        Assert.IsTrue(block.Rows[(int)corruptRecord + 1].IsInUse, "the record after it is imported");
    }

    [TestMethod]
    public void LiveBlockScan_NoInvalidFixup_SkipsNothing()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var imagePath = Path.Combine(_directory, "clean.img");
        SyntheticNtfsImage.Write(imagePath, 64);
        ServeImageAsVolume(imagePath);
        using var block = CreateBlock();

        var batches = LiveVolumeSources.ScanDriveRecordBatches("C", new ParseThreadAllowance(1), new QuietReporter(),
            null, default, CancellationToken.None).ToArray();
        var written = MftBlockScan.WriteToBlock(block, new BlockStamp(default, () => DateTime.UtcNow), batches,
            MftBlockRowFilter.Full, new BlockWriteReporting(null, null), CancellationToken.None);

        Assert.IsFalse(batches.Any(batch => batch is MftOmittedRecords));
        Assert.AreEqual(0L, written.SkippedRecordCount);
        Assert.IsTrue(block.Header.RowCount > 5);
    }

    [TestMethod]
    public void BlockScan_OmittedRecordsBatch_AddsItsCountToTheSkippedRecords()
    {
        using var block = CreateBlock();
        IReadOnlyList<MftRecord>[] batches =
        [
            [Record(5, 5, isDirectory: true), Record(7, 5), Record(uint.MaxValue + 1UL, 5)],
            new MftOmittedRecords(3)
        ];

        var written = MftBlockScan.WriteToBlock(block, new BlockStamp(default, () => DateTime.UtcNow), batches,
            MftBlockRowFilter.Full, new BlockWriteReporting(null, null), CancellationToken.None);

        Assert.AreEqual(4L, written.SkippedRecordCount, "one record the writer could not place and three left out");
        Assert.AreEqual(8L, written.RowCount);
        Assert.AreEqual(0, new MftOmittedRecords(3).Count, "the batch itself carries no record");
    }

    [DataTestMethod]
    [DataRow("F1-1", false)]
    [DataRow("F2-1", true)]
    public void VolumeParse_BootstrapRecordWithAnInvalidFixup_FailsWithoutReturningRecords(string caseId,
        bool corruptLayout)
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var imagePath = Path.Combine(_directory, $"corrupt-bootstrap-{caseId}.img");
        SyntheticNtfsImage.Write(imagePath, 64);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(RecordSize);

        var image = File.ReadAllBytes(imagePath);
        if (corruptLayout)
        {
            image[ClusterSize + 6] = 0x01; // update sequence array size 1, not one entry per sector plus its number
        }
        else
        {
            image[ClusterSize + 510] ^= 0xFF; // record zero's first-sector tail is not its update sequence number
        }

        File.WriteAllBytes(imagePath, image);
        using var handle = File.OpenHandle(imagePath);

        var resultPointer = MFTLibNative._parseMftRecordsWithProgress(handle, false, 64, IntPtr.Zero,
            null);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            Assert.AreEqual("MFT record 0 has an invalid fixup", result.ErrorMessage);
            Assert.AreEqual(0UL, result.UsedRecords, "a rejected bootstrap record supplies no records");
            Assert.AreEqual(0UL, result.InvalidFixupRecords, "record zero is never reached by the batch parser");
        }
        finally
        {
            MFTLibNative._freeMftResult(resultPointer);
        }
    }

    // A 64-record synthetic volume in which one record that has a row, and whose two neighbours
    // have rows too, is given a first-sector tail that is not its update sequence number.
    (string ImagePath, ulong CorruptRecord) WriteImageWithOneCorruptFixup()
    {
        var imagePath = Path.Combine(_directory, "corrupt.img");
        SyntheticNtfsImage.Write(imagePath, 64);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(RecordSize);
        var imported = ImportedRecordNumbers(imagePath);
        var corruptRecord = imported.First(number =>
            number > 16 && imported.Contains(number - 1) && imported.Contains(number + 1));

        var image = File.ReadAllBytes(imagePath);
        image[ClusterSize + (int)corruptRecord * RecordSize + 510] ^= 0xFF;
        File.WriteAllBytes(imagePath, image);
        return (imagePath, corruptRecord);
    }

    static HashSet<ulong> ImportedRecordNumbers(string imagePath)
    {
        using var image = File.OpenHandle(imagePath);
        using var result = new MftResult(
            MFTLibNative._parseMftRecordsWithProgress(image, false, 64, IntPtr.Zero, null));
        Assert.AreEqual(0UL, result.InvalidFixupRecordCount, "the untouched image has no invalid fixup");
        return result.ToArray().Where(record => record.InUse).Select(record => record.RecordNumber).ToHashSet();
    }

    // Every volume open reads the image, and the volume query reports its record size.
    static void ServeImageAsVolume(string imagePath)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(RecordSize);
        FileUtilities._getVolumeHandle = _ => File.OpenHandle(imagePath);
        Kernel32._deviceIoControl = (_, _, _, _, outBuffer, _, out bytesReturned, _) =>
        {
            Marshal.StructureToPtr(new NtfsVolumeDataBufferNative
            {
                MftValidDataLength = 64 * RecordSize,
                BytesPerFileRecordSegment = RecordSize
            }, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
    }

    BlockFile CreateBlock() => BlockFile.Create(new BlockFileCreateOptions
    {
        Path = Path.Combine(_directory, $"{Guid.NewGuid():N}.mlix"),
        VolumeSerial = 0,
        ProducerKind = ProducerKind.Mft,
        RootRow = 5,
        SlotCapacity = 128,
        NamePoolCapacity = 16 * 1024,
        DeleteOnClose = true
    });

    static MftRecord Record(ulong recordNumber, ulong parent, bool isDirectory = false) =>
        MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parent,
            IsDirectory = isDirectory,
            FileName = $"record-{recordNumber}"
        });

    sealed class QuietReporter : IBrokerOperationReporter
    {
        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName)
        {
        }
    }
}
