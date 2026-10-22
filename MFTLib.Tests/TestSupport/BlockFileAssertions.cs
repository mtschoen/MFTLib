using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

static class BlockFileAssertions
{
    public static void IsDisposed(BlockFile block)
    {
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = block.Header.RowCount);
    }
}
