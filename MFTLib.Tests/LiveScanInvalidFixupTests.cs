using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.NativeInterop;
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
public sealed class LiveScanInvalidFixupTests : IDisposable
{
    const int ClusterSize = 4096;
    const int RecordSize = 1024;

    TempDirectory _tempDirectory = null!;
    string TemporaryDirectoryPath => _tempDirectory.Path;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = new TempDirectory("mftlib-dump");
    }

    public void Dispose()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
        Kernel32.Hooks.ResetToDefaults();
        _tempDirectory.Dispose();
    }

    [TestMethod]
    public void VolumeParse_AllocatedRecordWithAnInvalidFixup_IsCountedAndItsNeighboursAreReturned()
    {
        var (imagePath, corruptRecord) = WriteImageWithOneCorruptFixup();
        using var image = File.OpenHandle(imagePath);

        var resultPointer = MFTLibNative.Hooks._parseMftRecordsWithProgress(image, false, 64, IntPtr.Zero,
            default);
        var counted = Marshal.PtrToStructure<MftParseResult>(resultPointer);
        using var result = new MftResult(resultPointer);
        var recordNumbers = result.ToRecords().Select(record => record.RecordNumber).ToArray();

        Assert.AreEqual(string.Empty, counted.ErrorMessage);
        Assert.AreEqual(0u, counted.InvalidInput, "a live scan never fails on the record");
        Assert.AreEqual(1UL, counted.InvalidFixupRecords);
        Assert.AreEqual(1UL, result.InvalidFixupRecordCount);
        Assert.DoesNotContain(corruptRecord, recordNumbers);
        Assert.Contains(corruptRecord - 1, recordNumbers);
        Assert.Contains(corruptRecord + 1, recordNumbers);
    }

    [TestMethod]
    public void LiveScan_AllocatedRecordWithAnInvalidFixup_IsReportedAsOmitted()
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, corruptRecord) = WriteImageWithOneCorruptFixup();
        ServeImageAsVolume(imagePath);
        using var block = CreateBlock();

        long omitted = 0;
        var batches = LiveVolumeSources.ScanDriveRecordBatches('C', new ParseThreadAllowance(2),
            new BlockWriteReporting(null, new QuietReporter()), new MftScanOptions(), count => omitted += count, CancellationToken.None);
        var written = MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), batches,
            new MftRowSelection(new MftScanOptions()), new BlockWriteReporting(null, null), CancellationToken.None);

        Assert.AreEqual(1L, omitted);
        Assert.AreEqual(0L, written.SkippedRecordCount, "the source reports the record; the producer adds it");
        Assert.IsFalse(block.Rows[(int)corruptRecord].IsInUse, "the corrupt record has no row");
        Assert.IsTrue(block.Rows[(int)corruptRecord - 1].IsInUse, "the record before it is imported");
        Assert.IsTrue(block.Rows[(int)corruptRecord + 1].IsInUse, "the record after it is imported");
    }

    [TestMethod]
    public async Task FromLocalVolumes_AllocatedRecordWithAnInvalidFixup_IsReportedAsASkippedRecord()
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, corruptRecord) = WriteImageWithOneCorruptFixup();
        ServeImageAsVolume(imagePath);
        var request = new MftBlockProduceRequest
        {
            Drive = 'C',
            VolumeSerial = 0,
            BlockPath = Path.Combine(TemporaryDirectoryPath, "local.mlix"),
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
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var imagePath = Path.Combine(TemporaryDirectoryPath, "clean.img");
        SyntheticNtfsImage.Write(imagePath, 64);
        ServeImageAsVolume(imagePath);
        using var block = CreateBlock();

        long omitted = 0;
        var batches = LiveVolumeSources.ScanDriveRecordBatches('C', new ParseThreadAllowance(1),
            new BlockWriteReporting(null, new QuietReporter()), new MftScanOptions(), count => omitted += count, CancellationToken.None);
        var written = MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), batches,
            new MftRowSelection(new MftScanOptions()), new BlockWriteReporting(null, null), CancellationToken.None);

        Assert.AreEqual(0L, omitted);
        Assert.AreEqual(0L, written.SkippedRecordCount);
        Assert.IsGreaterThan(5u, block.Header.RowCount);
    }


    [TestMethod]
    [DataRow("F1-1", false)]
    [DataRow("F2-1", true)]
    public void VolumeParse_BootstrapRecordWithAnInvalidFixup_FailsWithoutReturningRecords(string caseId,
        bool corruptLayout)
    {
        var imagePath = Path.Combine(TemporaryDirectoryPath, $"corrupt-bootstrap-{caseId}.img");
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

        var resultPointer = MFTLibNative.Hooks._parseMftRecordsWithProgress(handle, false, 64, IntPtr.Zero,
            default);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            Assert.AreEqual("MFT record 0 has an invalid fixup", result.ErrorMessage);
            Assert.AreEqual(0UL, result.UsedRecords, "a rejected bootstrap record supplies no records");
            Assert.AreEqual(0UL, result.InvalidFixupRecords, "record zero is never reached by the batch parser");
        }
        finally
        {
            MFTLibNative.Hooks._freeMftResult(resultPointer);
        }
    }

    // A 64-record synthetic volume in which one record that has a row, and whose two neighbours
    // have rows too, is given a first-sector tail that is not its update sequence number.
    (string ImagePath, ulong CorruptRecord) WriteImageWithOneCorruptFixup()
    {
        var imagePath = Path.Combine(TemporaryDirectoryPath, "corrupt.img");
        SyntheticNtfsImage.Write(imagePath, 64);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(RecordSize);
        var imported = ImportedRecordNumbers(imagePath);
        var corruptRecord = imported.First(number =>
            number > 16 && imported.Contains(number - 1) && imported.Contains(number + 1));

        var image = File.ReadAllBytes(imagePath);
        image[ClusterSize + ((int)corruptRecord * RecordSize) + 510] ^= 0xFF;
        File.WriteAllBytes(imagePath, image);
        return (imagePath, corruptRecord);
    }

    static HashSet<ulong> ImportedRecordNumbers(string imagePath)
    {
        using var image = File.OpenHandle(imagePath);
        using var result = new MftResult(
            MFTLibNative.Hooks._parseMftRecordsWithProgress(image, false, 64, IntPtr.Zero, default));
        Assert.AreEqual(0UL, result.InvalidFixupRecordCount, "the untouched image has no invalid fixup");
        return [.. result.ToRecords().Where(record => record.InUse).Select(record => record.RecordNumber)];
    }

    // Every volume open reads the image, and the volume query reports its record size.
    static void ServeImageAsVolume(string imagePath)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(RecordSize);
        FileUtilities.Hooks._getVolumeHandle = _ => File.OpenHandle(imagePath);
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, outBuffer, _, out bytesReturned, _) =>
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
        Path = Path.Combine(TemporaryDirectoryPath, $"{Guid.NewGuid():N}.mlix"),
        VolumeSerial = 0,
        ProducerKind = ProducerKind.Mft,
        RootRow = 5,
        SlotCapacity = 128,
        NamePoolCapacity = 16 * 1024,
        DeleteOnClose = true
    });

    sealed class QuietReporter : ISourceOperationReporter
    {
        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName)
        {
        }
    }
}
