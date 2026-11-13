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
}
