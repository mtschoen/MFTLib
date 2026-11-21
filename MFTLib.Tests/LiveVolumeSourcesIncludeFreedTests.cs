using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The production volume source asks the native parse for freed records only when the scan options say so, and
///     keeps the not-in-use records it gets back only then.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class LiveVolumeSourcesIncludeFreedTests : IDisposable
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
    public void OpenVolumeScan_KeepsFreedRecordsOnlyWhenAsked(bool includeFreed)
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, freedRecord) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath);
        using var block = TestBlocks.Create(Path.Combine(TemporaryDirectoryPath, "freed-option.mlix"), ProducerKind.Mft, 128, 16 * 1024, volumeSerial: 0, rootRow: 5, deleteOnClose: true);
        var requests = new List<bool>();
        var parse = MFTLibNative.Hooks._parseMftRecordsStreaming;
        MFTLibNative.Hooks._parseMftRecordsStreaming = (handle, freed, bufferSize, control, callback, sink) =>
        {
            requests.Add(freed);
            return parse(handle, freed, bufferSize, control, callback, sink);
        };

        var options = new MftScanOptions { IncludeFreed = includeFreed };
        using var source = LiveVolumeSources.OpenVolumeScan('C', new ParseThreadAllowance(2),
            new BlockWriteReporting(null, new QuietReporter()), options, expectedVolumeSerial: 0, CancellationToken.None);
        MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), source, options,
            new BlockWriteReporting(null, null), CancellationToken.None);

        Assert.AreSequenceEqual([includeFreed], requests);
        Assert.AreEqual(includeFreed, block.Rows[(int)freedRecord].IsDeleted);
        Assert.IsTrue(block.Rows[(int)freedRecord + 1].IsInUse, "Live records still arrive with the option on.");
    }

    [TestMethod]
    public void OpenVolumeScan_RejectsADifferentSerialOnTheScanHandle()
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, _) = WriteImageWithOneFreedRecord();
        // The serials share their low 32 bits.
        ServeImageAsVolume(imagePath, scanSerial: 0x3333333322222222UL, sizingSerial: 0x1111111122222222UL);
        Assert.ThrowsExactly<IOException>(() =>
        {
            using var source = LiveVolumeSources.OpenVolumeScan('C', new ParseThreadAllowance(2),
                new BlockWriteReporting(null, null), new MftScanOptions(), expectedVolumeSerial: 0x1111111122222222UL,
                CancellationToken.None);
        });
    }

    // The first pair shares its low 32 bits, so only a comparison of the full serial tells the volumes apart. A block
    // that records serial zero cannot establish which volume it was scanned from, even when the volume reports zero.
    [TestMethod]
    [DataRow(0x1111111122222222UL, 0x3333333322222222UL)]
    [DataRow(0UL, 0UL)]
    public async Task ReadInlineContent_OfAVolumeTheBlockCannotBeBoundTo_FailsBeforeAnyAnswer(ulong blockSerial,
        ulong volumeSerial)
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, _) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath, scanSerial: volumeSerial, sizingSerial: volumeSerial);
        await using var index = await InlineContentIndexes.OpenVolumeAsync(
            MftIndexSources.FromLocalVolumes().InlineContentReader, blockSerial, TemporaryDirectoryPath);
        var calls = 0;

        // A directory is answered without I/O and a file needs a record read: neither may reach the handler.
        var result = await index.ReadInlineContentAsync(
            [InlineContentIndexes.Entry(index, 6), InlineContentIndexes.Entry(index, 7)], _ => calls++,
            CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Failed, result.Outcome);
        Assert.IsInstanceOfType<IOException>(result.Failure);
        Assert.AreEqual(0, calls, "a refused read delivers no answer");
    }

    [TestMethod]
    public async Task ReadInlineContent_OfTheVolumeWithTheBlocksFullSerial_AnswersEveryEntry()
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        const ulong serial = 0x3333333322222222UL;
        var (imagePath, _) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath, scanSerial: serial, sizingSerial: serial);
        await using var index = await InlineContentIndexes.OpenVolumeAsync(
            MftIndexSources.FromLocalVolumes().InlineContentReader, serial, TemporaryDirectoryPath);
        var calls = 0;

        var result = await index.ReadInlineContentAsync(
            [InlineContentIndexes.Entry(index, 6), InlineContentIndexes.Entry(index, 7)], _ => calls++,
            CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome, result.Failure?.ToString());
        Assert.AreEqual(2, calls);
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

        using var source = LiveVolumeSources.OpenVolumeScan('C', new ParseThreadAllowance(2),
            new BlockWriteReporting(null, new QuietReporter()), new MftScanOptions { IncludeFreed = true }, expectedVolumeSerial: 0, CancellationToken.None);
        MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), source,
            new MftScanOptions { IncludeFreed = true }, new BlockWriteReporting(null, null),
            CancellationToken.None);

        Assert.IsTrue(block.Rows[(int)freedRecord].IsDeleted);
        Assert.IsFalse(block.Rows[(int)freedRecord + 1].IsDeleted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StreamedScan_WritesTheBlockAWholeResultParseWrites(bool includeFreed)
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, _) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath);
        var options = new MftScanOptions { IncludeFreed = includeFreed };
        using var streamed = TestBlocks.Create(Path.Combine(TemporaryDirectoryPath, "streamed.mlix"), ProducerKind.Mft, 128, 16 * 1024, volumeSerial: 0, rootRow: 5, deleteOnClose: true);
        using var whole = TestBlocks.Create(Path.Combine(TemporaryDirectoryPath, "whole.mlix"), ProducerKind.Mft, 128, 16 * 1024, volumeSerial: 0, rootRow: 5, deleteOnClose: true);

        using (var source = LiveVolumeSources.OpenVolumeScan('C', new ParseThreadAllowance(2),
                   new BlockWriteReporting(null, new QuietReporter()), options, expectedVolumeSerial: 0, CancellationToken.None))
        {
            MftBlockScan.WriteToBlock(streamed, new BlockStamp(default, TimeProvider.System), source, options,
                new BlockWriteReporting(null, null), CancellationToken.None);
        }

        using (var volume = MftVolume.Open('C', 64))
        // ReSharper disable once AccessToDisposedClosure -- the batches are enumerated inside the using
        using (var source = new MftRecordSliceSource(omitted =>
                   volume.ReadRecordBatches(16, null, null, CancellationToken.None, options, omitted)))
        {
            MftBlockScan.WriteToBlock(whole, new BlockStamp(default, TimeProvider.System), source, options,
                new BlockWriteReporting(null, null), CancellationToken.None);
        }

        Assert.AreEqual(whole.Header.RowCount, streamed.Header.RowCount);
        Assert.IsTrue(MemoryMarshal.AsBytes(whole.Rows).SequenceEqual(MemoryMarshal.AsBytes(streamed.Rows)),
            "the rows are identical");
        Assert.IsTrue(whole.SequenceNumbers.SequenceEqual(streamed.SequenceNumbers), "the sequences are identical");
        for (uint row = 0; row < whole.Header.RowCount; row++)
        {
            Assert.AreEqual(NamePool.ReadRowName(whole, row).ToString(), NamePool.ReadRowName(streamed, row).ToString(),
                $"row {row} name");
        }
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

    static void ServeImageAsVolume(string imagePath, ulong scanSerial = 0, ulong sizingSerial = 0)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(RecordSize);
        Microsoft.Win32.SafeHandles.SafeFileHandle? sizingHandle = null;
        FileUtilities.Hooks._getVolumeHandle = _ =>
        {
            var handle = File.OpenHandle(imagePath);
            sizingHandle ??= handle;
            return handle;
        };
        Kernel32.Hooks._deviceIoControl = (device, _, _, _, outBuffer, _, out bytesReturned, _) =>
        {
            Marshal.StructureToPtr(new NtfsVolumeDataBufferNative
            {
                MftValidDataLength = 64 * RecordSize,
                VolumeSerialNumber = unchecked((long)(ReferenceEquals(device, sizingHandle) ? sizingSerial : scanSerial)),
                BytesPerFileRecordSegment = RecordSize
            }, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
    }
}
