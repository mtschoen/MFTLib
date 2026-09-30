using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

[TestClass]
public class TestBodyCleanupTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RunAsync_PreservesPrimaryFailureAndReportsSecondary(
        bool bodyFails, bool cleanupFails)
    {
        var bodyFailure = new InvalidOperationException("body assertion failed");
        var cleanupFailure = new TimeoutException("runner did not leave");
        var diagnostics = new List<string>();
        bool cleanupFinished = false;
        Exception? observed = null;

        try
        {
            await TestBodyCleanup.RunAsync(
                () => bodyFails ? ThrowBodyAsync(bodyFailure) : Task.CompletedTask,
                async () =>
                {
                    cleanupFinished = true;
                    if (cleanupFails)
                    {
                        await ThrowCleanupAsync(cleanupFailure);
                    }
                },
                diagnostics.Add);
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        Assert.IsTrue(cleanupFinished);
        Exception? expected = bodyFails ? bodyFailure : cleanupFails ? cleanupFailure : null;
        if (expected == null)
        {
            Assert.IsNull(observed);
        }
        else
        {
            Assert.AreSame(expected, observed);
            StringAssert.Contains(observed!.StackTrace!,
                bodyFails ? nameof(ThrowBodyAsync) : nameof(ThrowCleanupAsync));
        }

        Assert.AreEqual(bodyFails && cleanupFails ? 1 : 0, diagnostics.Count);
        if (bodyFails && cleanupFails)
        {
            Assert.AreEqual("Secondary test cleanup failure: " + cleanupFailure, diagnostics[0]);
            StringAssert.Contains(diagnostics[0], nameof(ThrowCleanupAsync));
        }
    }

    static async Task ThrowBodyAsync(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }

    static async Task ThrowCleanupAsync(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }
}
