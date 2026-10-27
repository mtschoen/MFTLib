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
    [DataRow(false)]
    [DataRow(true)]
    public void ScanVolumeRecordBatches_KeepsFreedRecordsOnlyWhenAsked(bool includeFreed)
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, freedRecord) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath);
        var requests = new List<bool>();
        var parse = MFTLibNative.Hooks._parseMftRecordsWithProgress;
        MFTLibNative.Hooks._parseMftRecordsWithProgress = (handle, freed, bufferSize, control, callback) =>
        {
            requests.Add(freed);
            return parse(handle, freed, bufferSize, control, callback);
        };

        var records = LiveVolumeSources.ScanVolumeRecordBatches('C', new ParseThreadAllowance(2),
                new BlockWriteReporting(null, new QuietReporter()), new MftScanOptions { IncludeFreed = includeFreed }, _ => { }, CancellationToken.None)
            .SelectMany(batch => batch).ToArray();

        Assert.AreSequenceEqual([includeFreed], requests);
        var freed = records.Where(record => !record.InUse).ToArray();
        Assert.HasCount(includeFreed ? 1 : 0, freed);
        if (includeFreed)
        {
            Assert.AreEqual(freedRecord, freed[0].RecordNumber);
            Assert.Contains(record => record.InUse, records, "Live records still arrive with the option on.");
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
        using var block = TestBlocks.Create(Path.Combine(TemporaryDirectoryPath, "freed.mlix"), ProducerKind.Mft, 128, 16 * 1024, volumeSerial: 0, rootRow: 5, deleteOnClose: true);

        var batches = LiveVolumeSources.ScanVolumeRecordBatches('C', new ParseThreadAllowance(2),
            new BlockWriteReporting(null, new QuietReporter()), new MftScanOptions { IncludeFreed = true }, _ => { }, CancellationToken.None);
        MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), batches,
            new MftRowSelection(new MftScanOptions { IncludeFreed = true }), new BlockWriteReporting(null, null),
            CancellationToken.None);

        Assert.IsTrue(block.Rows[(int)freedRecord].IsDeleted);
        Assert.IsFalse(block.Rows[(int)freedRecord + 1].IsDeleted);
    }

    // A 64-record synthetic volume in which one imported record, whose next neighbour is imported too, is freed.
    (string ImagePath, ulong FreedRecord) WriteImageWithOneFreedRecord()
    {
        var imagePath = Path.Combine(TemporaryDirectoryPath, "freed.img");
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
            MFTLibNative.Hooks._parseMftRecordsWithProgress(image, false, 64, IntPtr.Zero, default));
        return [.. result.ToRecords().Where(record => record.InUse).Select(record => record.RecordNumber)];
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
}
