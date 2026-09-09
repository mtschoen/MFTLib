using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

public static class BlockFileAssertions
{
    public static void IsDisposed(BlockFile block)
    {
        Assert.ThrowsException<ObjectDisposedException>(() => _ = block.Header.RowCount);
    }
}
