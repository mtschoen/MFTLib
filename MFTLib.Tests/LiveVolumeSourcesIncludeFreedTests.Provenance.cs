using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Every MFT block at the current format version was scanned through a handle whose full serial matched its header.
// These run the production checks against the served image: the cache admission of a block from before the check,
// and the broker host's check on its scan handle.
public sealed partial class LiveVolumeSourcesIncludeFreedTests
{
    const ulong ServedSerial = 0x3333333322222222UL;

    /// <summary>The production volume access, with a fixed journal cursor so a cached scan needs no live journal.</summary>
    sealed class FixedCursorVolumeAccess : IVolumeAccess
    {
        static readonly LiveVolumeAccess Live = LiveVolumeAccess.Instance;

        public UsnJournalCursor QueryCursor(VolumeKey volume) => new(7, 4096);

        public IMftSliceSource OpenVolumeScan(VolumeKey volume, ParseThreadAllowance parseThreads,
            BlockWriteReporting reporting, MftScanOptions scanOptions, ulong expectedVolumeSerial,
            CancellationToken cancellationToken) =>
            Live.OpenVolumeScan(volume, parseThreads, reporting, scanOptions, expectedVolumeSerial, cancellationToken);

        public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(VolumeKey volume,
            UsnJournalCursor since, int maximumBufferReads) => ([], since);

        public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(VolumeKey volume,
            UsnJournalCursor since, ISourceOperationReporter operation, CancellationToken cancellationToken) =>
            throw new AssertFailedException("These tests watch no journal.");

        public NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume) => Live.QueryVolumeInformation(volume);

        [SupportedOSPlatform("windows")]
        public void ReadInlineContent(VolumeKey volume, ulong volumeSerial,
            ReadOnlyMemory<InlineContentRequest> requests, InlineContentSink sink, CancellationToken cancellationToken) =>
            Live.ReadInlineContent(volume, volumeSerial, requests, sink, cancellationToken);

        public UsnJournalGrowthResult GrowUsnJournal(VolumeKey volume, long maximumSize, long allocationDelta,
            bool allowAboveMaximumRecommendedSize = false) =>
            throw new AssertFailedException("These tests grow no journal.");
    }

    [TestMethod]
    public async Task WarmOpen_OfAFormatElevenBlock_ColdScansAndReadsThroughTheCheckedPath()
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, _) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath, scanSerial: ServedSerial, sizingSerial: ServedSerial);
        var volumeRoot = Directory.CreateDirectory(Path.Combine(TemporaryDirectoryPath, "volume")).FullName;
        var cacheDirectory = Path.Combine(TemporaryDirectoryPath, "cache");
        var options = SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = new VolumeKey('C'),
            CacheDirectory = cacheDirectory,
            MftSource = new LocalMftBlockProducer(TimeProvider.System, null, new FixedCursorVolumeAccess())
                .CreateIndexSource()
        }, ServedSerial, volume: new VolumeKey('C'), volumeRootDirectory: volumeRoot);
        await using (await FileIndex.OpenAsync(options, CancellationToken.None))
        {
        }

        // A version 11 block carries no proof that its scan checked the serial on its scan handle.
        var blockPath = Path.Combine(cacheDirectory, CacheDirectory.BlockFileName(new VolumeKey('C'), ServedSerial));
        var bytes = await File.ReadAllBytesAsync(blockPath);
        BitConverter.GetBytes(11u).CopyTo(bytes, 4);
        await File.WriteAllBytesAsync(blockPath, bytes);
        var diagnostics = new List<string>();
        await using var index = await FileIndex.OpenAsync(options with { Diagnostics = diagnostics.Add },
            CancellationToken.None);

        Assert.Contains(line => line.Contains($"cache validation failed: {BlockValidationResult.WrongFormatVersion}"),
            diagnostics);
        Assert.AreEqual(BlockOrigin.ProducedByScan, index.Status.Block!.Origin, "the old block is never adopted");
        var entries = index.Search(new SearchQuery(null));
        Assert.Contains(entry => !entry.IsDirectory, entries, "at least one entry needs a record read");
        var calls = 0;
        var result = await index.ReadInlineContentAsync(entries, _ => Interlocked.Increment(ref calls),
            CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome, result.Failure?.ToString());
        Assert.AreEqual(entries.Count, calls);
    }

    // The serials of the refused row share their low 32 bits; the matching row shows the scan is otherwise sound.
    [TestMethod]
    [DataRow(0x1111111122222222UL, false)]
    [DataRow(ServedSerial, true)]
    public async Task BrokerScan_ChecksTheFullSerialOnTheScanHandleBeforeAnyRow(ulong expectedSerial, bool accepted)
    {
        if (WindowsOnlyLiveScan.Skip())
        {
            return;
        }

        var (imagePath, _) = WriteImageWithOneFreedRecord();
        ServeImageAsVolume(imagePath, scanSerial: ServedSerial, sizingSerial: ServedSerial);
        var host = new JournalBrokerHost(new FixedCursorVolumeAccess(), processorCount: 2);
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);

        var pipe = await harness.OpenScanChannelAsync('C', expectedVolumeSerial: expectedSerial);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        if (accepted)
        {
            Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind, frames[^1].Message);
            Assert.IsGreaterThan(0u, blockWriter.Block.Header.RowCount);
            return;
        }

        Assert.AreEqual(BrokerFrameKind.Error, frames[^1].Kind);
        Assert.AreEqual("Volume C does not have the expected serial.", frames[^1].Message);
        Assert.IsFalse(frames.Any(frame => frame.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.ScanCompleted));
        Assert.IsNull(blockWriter.LastSectionName, "no row reached the block writer");
        Assert.AreEqual(0u, blockWriter.Block.Header.RowCount);
    }
}
