namespace MFTLib.Tests.TestSupport;

internal static class TestBodyCleanup
{
    // A body failure stays primary through the bare rethrow. Cleanup still runs;
    // its failure is reported in test output when secondary, and thrown when alone.
    public static async Task RunAsync(Func<Task> body, Func<Task> cleanup,
        Action<string> reportSecondaryFailure)
    {
        Exception? bodyFailure = null;
        try
        {
            await body();
        }
        catch (Exception exception)
        {
            bodyFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await cleanup();
            }
            catch (Exception exception) when (bodyFailure != null)
            {
                reportSecondaryFailure("Secondary test cleanup failure: " + exception);
            }
        }
    }
}
