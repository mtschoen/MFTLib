using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>Completion flushes a block to its file unless it was created delete-on-close.</summary>
[TestClass]
public sealed class BlockCompletionFlushTests : IDisposable
{
    readonly TempDirectory _tempDirectory = new("mftlib-completion-flush");

    public void Dispose() => _tempDirectory.Dispose();

    [TestMethod]
    [DataRow(true, false, DisplayName = "delete-on-close skips the flush")]
    [DataRow(false, true, DisplayName = "a kept block flushes")]
    public void Complete_FlushesOnlyABlockAnotherOpenCanReadBack(bool deleteOnClose, bool expectFlush)
    {
        using var block = TestBlocks.Create(Path.Combine(_tempDirectory.Path, $"{deleteOnClose}.mlix"),
            deleteOnClose: deleteOnClose);
        var flushedRanges = 0;

        new BlockWriter(block).Complete(DateTime.UtcNow, _ => flushedRanges++);

        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(deleteOnClose, block.DeletesOnClose);
        Assert.AreEqual(expectFlush, flushedRanges > 0);
    }

    // Opening by name does not inherit the client's delete-on-close flag. Keep flushing cached blocks before publication.
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    [DataRow(true, DisplayName = "uncached client block")]
    [DataRow(false, DisplayName = "cached client block")]
    public void BrokerSectionWrite_FlushesBeforeReturning(bool deleteOnClose)
    {
        var sectionName = NamedBlockSection.BuildSectionName('C');
        var (block, lifetime) = NamedBlockSection.Create(
            TestBlocks.Options(Path.Combine(_tempDirectory.Path, $"section-{deleteOnClose}.mlix"), ProducerKind.Mft,
                128, 1024, volumeSerial: 123, rootRow: 5, deleteOnClose: deleteOnClose),
            sectionName);
        using (block)
        using (lifetime)
        {
            var steps = new StepRecorder();
            using var source = RowWriting.Source([[new MftRecord(5, 5, new MftRecordFields(3), ".")]]);

            new RealBlockSectionWriter(TimeProvider.System).Write(sectionName,
                new UsnJournalCursor(7, 100), source, new MftScanOptions(), new BlockWriteReporting(null, steps),
                CancellationToken.None);

            Assert.IsTrue(block.Header.IsComplete);
            Assert.IsGreaterThan(0, steps.Count(MftBlockScan.FlushStep));
        }
    }

    sealed class StepRecorder : ISourceOperationReporter
    {
        readonly List<string> _steps = [];

        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName) => _steps.Add(stepName);

        public int Count(string stepName) => _steps.Count(step => step == stepName);
    }
}
