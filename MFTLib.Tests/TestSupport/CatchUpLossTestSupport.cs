using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

internal static class CatchUpLossTestSupport
{
    public static JournalCheckpointLoss StandardCatchUpLoss(char driveLetter) => new JournalCheckpointLoss(driveLetter, JournalCheckpointLossDetection.ScanCatchUp, JournalCheckpointLossCause.CheckpointTrimmed,
        new UsnJournalSettings { AllocationDelta = 4096, MaximumSize = 32768 })
    {
        CheckpointUsn = 1000,
        FirstUsn = 5000,
        NextUsn = 9000,
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    public static WatchHarnessScan Lost(char driveLetter, TestGate? hold = null) =>
        new(StandardCatchUpLoss(driveLetter), Hold: hold);

    public static JournalCatchUpLostException[] CatchUpLosses(WatchHarness harness, char driveLetter) =>
        harness.Faults.Where(fault => fault.Kind == WatchFaultKind.CatchUpLost && fault.Drive == driveLetter)
            .Select(fault => (JournalCatchUpLostException)fault.Exception).ToArray();
}
