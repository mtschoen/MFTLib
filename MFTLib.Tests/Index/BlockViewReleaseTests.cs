using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A writable view's disposal flushes every dirty page synchronously, so only a block somebody reads back
///     from disk may release its view that way.
/// </summary>
[TestClass]
public sealed class BlockViewReleaseTests : IDisposable
{
    readonly TempDirectory _tempDirectory = new("mftlib-view-release");

    public void Dispose() => _tempDirectory.Dispose();

    [TestMethod]
    [DataRow(true, false, DisplayName = "delete-on-close unmaps without flushing")]
    [DataRow(false, true, DisplayName = "a kept block flushes while unmapping")]
    public void Dispose_FlushesTheViewOnlyForAKeptBlock(bool deleteOnClose, bool expectFlush)
    {
        var block = TestBlocks.Create(Path.Combine(_tempDirectory.Path, $"{deleteOnClose}.mlix"),
            deleteOnClose: deleteOnClose);
        var releases = new List<bool>();
        block.Hooks._viewReleased = releases.Add;

        block.Dispose();

        CollectionAssert.AreEqual(new[] { expectFlush }, releases);
    }
}
