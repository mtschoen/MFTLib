using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

static class RootResultAssertions
{
    public static TException Failure<TException>(Func<RootOperationResult> operation)
        where TException : Exception
    {
        var result = operation();
        return RequireFailure<TException>(result);
    }

    public static async Task<TException> FailureAsync<TException>(
        Func<Task<RootOperationResult>> operation, string? message = null) where TException : Exception
    {
        var result = await operation();
        return RequireFailure<TException>(result, message);
    }

    static TException RequireFailure<TException>(RootOperationResult result, string? message = null)
        where TException : Exception
    {
        Assert.AreEqual(RootOperationOutcome.Failed, result.Outcome, message);
        Assert.IsInstanceOfType<TException>(result.Failure, message);
        return (TException)result.Failure!;
    }

    public static async Task<RootOperationResult> NotApplicableAsync(
        Func<Task<RootOperationResult>> operation, string? message = null)
    {
        var result = await operation();
        return RequireNotApplicable(result, message);
    }

    static RootOperationResult RequireNotApplicable(RootOperationResult result, string? message = null)
    {
        Assert.AreEqual(RootOperationOutcome.NotApplicable, result.Outcome, message);
        Assert.IsNull(result.Failure, message);
        return result;
    }
}
