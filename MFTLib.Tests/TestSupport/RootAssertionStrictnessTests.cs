using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

[TestClass]
public class RootAssertionStrictnessTests
{
    [TestMethod]
    public void Failure_ThrownExpectedException_DoesNotSatisfyResultAssertion()
    {
        var failure = new InvalidOperationException("root failure");

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() =>
            RootResultAssertions.Failure<InvalidOperationException>(() => throw failure));

        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailureAsync_ThrownExpectedException_DoesNotSatisfyResultAssertion(bool throwSynchronously)
    {
        var failure = new InvalidOperationException("root failure");

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            RootResultAssertions.FailureAsync<InvalidOperationException>(() => throwSynchronously
                ? throw failure
                : Task.FromException<RootOperationResult>(failure)));

        Assert.AreSame(failure, thrown);
    }
}
