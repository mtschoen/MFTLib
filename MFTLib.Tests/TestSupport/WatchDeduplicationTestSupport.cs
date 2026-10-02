using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

internal static class WatchDeduplicationTestSupport
{
    public static JournalCheckpointLoss StandardCatchUpLoss(char driveLetter) => new()
    {
        DriveLetter = driveLetter,
        DetectedDuring = JournalCheckpointLossDetection.ScanCatchUp,
        Cause = JournalCheckpointLossCause.CheckpointTrimmed,
        CheckpointUsn = 1000,
        FirstUsn = 5000,
        NextUsn = 9000,
        AllocationDelta = 4096,
        MaximumSize = 32768,
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    public static JournalCatchUpLostException[] CatchUpLosses(WatchHarness harness, char driveLetter) =>
        harness.Faults.Where(fault => fault.Kind == WatchFaultKind.CatchUpLost && fault.DriveLetter == driveLetter)
            .Select(fault => (JournalCatchUpLostException)fault.Exception).ToArray();

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new AssertFailedException($"Expected {typeof(TException).Name} to be thrown.");
    }

}
