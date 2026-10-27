using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>A root left with no block always reports why; a result without a reason is a library bug, not an empty message.</summary>
[TestClass]
public class PendingRootResultTests
{
    [TestMethod]
    public void RequireFailureMessage_ReturnsTheMessageTheResultCarries()
    {
        var result = new FileIndex.PendingRootResult { ProducerFailureMessage = "elevation declined" };

        Assert.AreEqual("elevation declined", result.RequireFailureMessage(new VolumeKey('T')));
    }

    [TestMethod]
    public void RequireFailureMessage_WithoutAMessage_ThrowsNamingTheRoot()
    {
        var result = new FileIndex.PendingRootResult();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => result.RequireFailureMessage(new VolumeKey('T')));

        Assert.Contains("Root T", exception.Message);
    }
}
