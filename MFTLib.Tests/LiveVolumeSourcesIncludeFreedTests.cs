using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The production volume source asks the native parse for freed records only when the scan options say so, and
///     keeps the not-in-use records it gets back only then.
/// </summary>
[TestClass]
[DoNotParallelize]
public class LiveVolumeSourcesIncludeFreedTests
{
    const int ClusterSize = 4096;
    const int RecordSize = 1024;
    const int RecordFlagsOffset = 0x16;

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

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScanDriveRecordBatches_KeepsFreedRecordsOnlyWhenAsked(bool includeFreed)
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var (imagePath, freedRecord) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath);
        var requests = new List<bool>();
        var parse = MFTLibNative._parseMftRecordsWithProgress;
        MFTLibNative._parseMftRecordsWithProgress = (handle, freed, bufferSize, control, callback) =>
        {
            requests.Add(freed);
            return parse(handle, freed, bufferSize, control, callback);
        };

        var records = LiveVolumeSources.ScanDriveRecordBatches("C", new ParseThreadAllowance(2), new QuietReporter(),
                null, new MftRecordScanOptions { IncludeFreed = includeFreed }, CancellationToken.None)
            .SelectMany(batch => batch).ToArray();

        CollectionAssert.AreEqual(new[] { includeFreed }, requests);
        var freed = records.Where(record => !record.InUse).ToArray();
        Assert.AreEqual(includeFreed ? 1 : 0, freed.Length);
        if (includeFreed)
        {
            Assert.AreEqual(freedRecord, freed[0].RecordNumber);
            Assert.IsTrue(records.Any(record => record.InUse), "Live records still arrive with the option on.");
        }
    }

    [TestMethod]
    public void BlockScan_WithTheOption_WritesTheFreedRecordAsADeletedRow()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var (imagePath, freedRecord) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath);
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(_directory, "freed.mlix"),
            VolumeSerial = 0,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 128,
            NamePoolCapacity = 16 * 1024,
            DeleteOnClose = true
        });

        var batches = LiveVolumeSources.ScanDriveRecordBatches("C", new ParseThreadAllowance(2), new QuietReporter(),
            null, new MftRecordScanOptions { IncludeFreed = true }, CancellationToken.None);
        MftBlockScan.WriteToBlock(block, new BlockStamp(default, () => DateTime.UtcNow), batches,
            new MftBlockRowFilter(BrokerScanProfile.Full, IncludeFreed: true), new BlockWriteReporting(null, null),
            CancellationToken.None);

        Assert.IsTrue(block.Rows[(int)freedRecord].IsDeleted);
        Assert.IsFalse(block.Rows[(int)freedRecord + 1].IsDeleted);
    }

    // A 64-record synthetic volume in which one imported record, whose next neighbour is imported too, is freed.
    (string ImagePath, ulong FreedRecord) WriteImageWithOneFreedRecord()
    {
        var imagePath = Path.Combine(_directory, "freed.img");
        SyntheticNtfsImage.Write(imagePath, 64);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(RecordSize);
        var imported = ImportedRecordNumbers(imagePath);
        var freedRecord = imported.First(number => number > 16 && imported.Contains(number + 1));

        var image = File.ReadAllBytes(imagePath);
        var flagsOffset = ClusterSize + ((int)freedRecord * RecordSize) + RecordFlagsOffset;
        image[flagsOffset] = (byte)(image[flagsOffset] & ~1);
        File.WriteAllBytes(imagePath, image);
        return (imagePath, freedRecord);
    }

    static HashSet<ulong> ImportedRecordNumbers(string imagePath)
    {
        using var image = File.OpenHandle(imagePath);
        using var result = new MftResult(
            MFTLibNative._parseMftRecordsWithProgress(image, false, 64, IntPtr.Zero, null));
        return result.ToArray().Where(record => record.InUse).Select(record => record.RecordNumber).ToHashSet();
    }

    sealed class QuietReporter : IBrokerOperationReporter
    {
        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName)
        {
        }
    }

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
}
