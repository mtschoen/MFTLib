using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

[TestClass]
public class DriveAssertionStrictnessTests
{
    [TestMethod]
    public void Failure_ThrownExpectedException_DoesNotSatisfyResultAssertion()
    {
        var failure = new InvalidOperationException("drive failure");

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() =>
            DriveResultAssertions.Failure<InvalidOperationException>(() => throw failure));

        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailureAsync_ThrownExpectedException_DoesNotSatisfyResultAssertion(bool throwSynchronously)
    {
        var failure = new InvalidOperationException("drive failure");

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DriveResultAssertions.FailureAsync<InvalidOperationException>(() => throwSynchronously
                ? throw failure
                : Task.FromException<DriveOperationResult>(failure)));

        Assert.AreSame(failure, thrown);
    }
}
