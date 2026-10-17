using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The production volume source asks the native parse for freed records only when the scan options say so, and
///     keeps the not-in-use records it gets back only then.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveVolumeSourcesIncludeFreedTests : IDisposable
{
    const int ClusterSize = 4096;
    const int RecordSize = 1024;
    const int RecordFlagsOffset = 0x16;

    TempDirectory _tempDirectory = null!;
    string _directory => _tempDirectory.Path;

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

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScanDriveRecordBatches_KeepsFreedRecordsOnlyWhenAsked(bool includeFreed)
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, freedRecord) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath);
        var requests = new List<bool>();
        var parse = MFTLibNative.Hooks.ParseMftRecordsWithProgress;
        MFTLibNative.Hooks.ParseMftRecordsWithProgress = (handle, freed, bufferSize, control, callback) =>
        {
            requests.Add(freed);
            return parse(handle, freed, bufferSize, control, callback);
        };

        var records = LiveVolumeSources.ScanDriveRecordBatches('C', new ParseThreadAllowance(2),
                new BlockWriteReporting(null, new QuietReporter()), new MftScanOptions { IncludeFreed = includeFreed }, _ => { }, CancellationToken.None)
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
        if (WindowsOnlyLiveScan.Skip())
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

        var batches = LiveVolumeSources.ScanDriveRecordBatches('C', new ParseThreadAllowance(2),
            new BlockWriteReporting(null, new QuietReporter()), new MftScanOptions { IncludeFreed = true }, _ => { }, CancellationToken.None);
        MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), batches,
            new MftScanOptions { IncludeFreed = true }, new BlockWriteReporting(null, null),
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
            MFTLibNative.Hooks.ParseMftRecordsWithProgress(image, false, 64, IntPtr.Zero, null));
        return result.ToRecords().Where(record => record.InUse).Select(record => record.RecordNumber).ToHashSet();
    }

    sealed class QuietReporter : ISourceOperationReporter
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
        FileUtilities.Hooks.GetVolumeHandle = _ => File.OpenHandle(imagePath);
        Kernel32.Hooks.DeviceIoControl = (_, _, _, _, outBuffer, _, out bytesReturned, _) =>
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
