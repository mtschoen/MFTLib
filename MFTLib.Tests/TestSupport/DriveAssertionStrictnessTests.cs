using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

[TestClass]
public class DriveAssertionStrictnessTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ThrowsAsync_CompletedFailedResult_DoesNotSatisfyThrowAssertion(bool eraseResultType)
    {
        IReadOnlyList<DriveOperationResult> results =
            [new('T', DriveOperationOutcome.Failed, new InvalidOperationException("drive failure"))];
        var completed = Task.FromResult(results);

        if (eraseResultType)
        {
            Func<Task> operation = () => completed;
            await Assert.ThrowsExceptionAsync<AssertFailedException>(() =>
                WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(operation));
        }
        else
        {
            await Assert.ThrowsExceptionAsync<AssertFailedException>(() =>
                WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() => completed));
        }
    }

    [TestMethod]
    public void Failure_ThrownExpectedException_DoesNotSatisfyResultAssertion()
    {
        var failure = new InvalidOperationException("drive failure");

        var thrown = Assert.ThrowsException<InvalidOperationException>(() =>
            DriveResultAssertions.Failure<InvalidOperationException>(() => throw failure));

        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailureAsync_ThrownExpectedException_DoesNotSatisfyResultAssertion(bool throwSynchronously)
    {
        var failure = new InvalidOperationException("drive failure");

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            DriveResultAssertions.FailureAsync<InvalidOperationException>(() => throwSynchronously
                ? throw failure
                : Task.FromException<IReadOnlyList<DriveOperationResult>>(failure)));

        Assert.AreSame(failure, thrown);
    }
}
