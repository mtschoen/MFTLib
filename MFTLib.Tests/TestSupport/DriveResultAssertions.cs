using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

static class DriveResultAssertions
{
    public static TException Failure<TException>(Func<IReadOnlyList<DriveOperationResult>> operation)
        where TException : Exception
    {
        var results = operation();
        return RequireFailure<TException>(results);
    }

    public static async Task<TException> FailureAsync<TException>(
        Func<Task<IReadOnlyList<DriveOperationResult>>> operation, string? message = null) where TException : Exception
    {
        var results = await operation();
        return RequireFailure<TException>(results, message);
    }

    static TException RequireFailure<TException>(IReadOnlyList<DriveOperationResult> results, string? message = null)
        where TException : Exception
    {
        Assert.HasCount(1, results, message);
        Assert.AreEqual(DriveOperationOutcome.Failed, results[0].Outcome, message);
        Assert.IsInstanceOfType<TException>(results[0].Failure, message);
        return (TException)results[0].Failure!;
    }

    public static async Task<DriveOperationResult> NotApplicableAsync(
        Func<Task<IReadOnlyList<DriveOperationResult>>> operation, string? message = null)
    {
        var results = await operation();
        Assert.HasCount(1, results, message);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results[0].Outcome, message);
        Assert.IsNull(results[0].Failure, message);
        return results[0];
    }
}
