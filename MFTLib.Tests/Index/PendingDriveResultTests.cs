using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>A drive left with no block always reports why; a result without a reason is a library bug, not an empty message.</summary>
[TestClass]
public class PendingDriveResultTests
{
    [TestMethod]
    public void RequireFailureMessage_ReturnsTheMessageTheResultCarries()
    {
        var result = new FileIndex.PendingDriveResult { ProducerFailureMessage = "elevation declined" };

        Assert.AreEqual("elevation declined", result.RequireFailureMessage('T'));
    }

    [TestMethod]
    public void RequireFailureMessage_WithoutAMessage_ThrowsNamingTheDrive()
    {
        var result = new FileIndex.PendingDriveResult();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => result.RequireFailureMessage('T'));

        StringAssert.Contains(exception.Message, "Drive T");
    }
}
