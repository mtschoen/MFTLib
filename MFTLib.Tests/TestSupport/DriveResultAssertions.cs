using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

static class DriveResultAssertions
{
    public static TException Failure<TException>(Func<DriveOperationResult> operation)
        where TException : Exception
    {
        var result = operation();
        return RequireFailure<TException>(result);
    }

    public static async Task<TException> FailureAsync<TException>(
        Func<Task<DriveOperationResult>> operation, string? message = null) where TException : Exception
    {
        var result = await operation();
        return RequireFailure<TException>(result, message);
    }

    static TException RequireFailure<TException>(DriveOperationResult result, string? message = null)
        where TException : Exception
    {
        Assert.AreEqual(DriveOperationOutcome.Failed, result.Outcome, message);
        Assert.IsInstanceOfType<TException>(result.Failure, message);
        return (TException)result.Failure!;
    }

    public static async Task<DriveOperationResult> NotApplicableAsync(
        Func<Task<DriveOperationResult>> operation, string? message = null)
    {
        var result = await operation();
        return RequireNotApplicable(result, message);
    }

    static DriveOperationResult RequireNotApplicable(DriveOperationResult result, string? message = null)
    {
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, result.Outcome, message);
        Assert.IsNull(result.Failure, message);
        return result;
    }
}
