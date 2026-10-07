using MFTLib.Index;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// What both samples print about a drive that settled, and the scan phases they report as the scan runs.
partial class SampleHost
{
    // An offline or failed drive settles without a usable index, so a verb stops here with the reason.
    static void ThrowIfNotReady(DriveStatus status)
    {
        if (status.State == DriveState.Failed)
        {
            throw new InvalidOperationException(status.MftProducerFailureMessage ?? $"The drive failed: {status.FailureKind}");
        }

        // An offline drive settles without a scan, so there is no catch-up to report.
        if (status.State == DriveState.Offline)
        {
            throw new InvalidOperationException("The drive is offline; nothing was scanned.");
        }
    }

    void WriteStatus(DriveStatus status)
    {
        ThrowIfNotReady(status);
        _writeLine($"Index holds {status.LiveRowCount} rows; {status.SkippedRecordCount} records skipped");
        _writeLine(status.CheckpointLoss is { } loss
            ? $"Catch-up lost: {loss.Cause}"
            : $"Catch-up held; watch supported: {status.WatchSupported}");
    }

    // Reports each scan phase once, on the reporting thread, so the output keeps its order.
    sealed class PhaseReporter(Action<string> writeLine) : IProgress<IndexScanProgress>
    {
        IndexScanPhase? _lastPhase;

        public void Report(IndexScanProgress value)
        {
            if (_lastPhase == value.Phase)
            {
                return;
            }

            _lastPhase = value.Phase;
            writeLine($"  {value.Phase}: {value.RowsWritten} rows");
        }
    }
}
