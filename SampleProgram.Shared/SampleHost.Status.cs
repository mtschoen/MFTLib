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
    static DriveBlockStatus RequireReadyBlock(DriveStatus status)
    {
        if (status is { State: DriveState.Failed, Failure: { } failure })
        {
            throw new InvalidOperationException($"The drive failed ({failure.Kind}): {failure.Message}");
        }

        // An offline drive settles without a scan, so there is no catch-up to report.
        return status.Block ?? throw new InvalidOperationException("The drive is offline; nothing was scanned.");
    }

    void WriteStatus(DriveStatus status)
    {
        var block = RequireReadyBlock(status);
        _writeLine($"Index holds {block.LiveRowCount} rows; {block.SkippedRecordCount} records skipped");
        _writeLine(status.Watch.CheckpointLoss is { } loss
            ? $"Catch-up lost: {loss.Cause}"
            : $"Catch-up held; watch supported: {status.Watch.IsSupported}");
    }

    // Reports each scan phase when it starts and again each time another ProgressRowInterval rows were written within it,
    // on the reporting thread so the output keeps its order. The interval counts rows, never time.
    internal sealed class PhaseReporter(Action<string> writeLine) : IProgress<IndexScanProgress>
    {
        internal const uint ProgressRowInterval = 250_000;

        IndexScanPhase? _lastPhase;
        uint _lastReportedRows;

        public void Report(IndexScanProgress value)
        {
            var sameSpan = _lastPhase == value.Phase;
            if (sameSpan && value.WrittenRowCount - _lastReportedRows < ProgressRowInterval)
            {
                return;
            }

            _lastPhase = value.Phase;
            _lastReportedRows = value.WrittenRowCount;
            writeLine($"  {value.Drive}: {value.Phase}: {value.WrittenRowCount} rows{(value.ExpectedRowCount is { } total ? $" of {total}" : string.Empty)}{(value.Outcome is { } outcome ? $" ({outcome})" : string.Empty)}{(value.CurrentDirectory is { } directory ? $" in {directory}" : string.Empty)}");
        }
    }

    // Reports each drive as it settles, one self-describing line per report. Reports arrive synchronously on the settling
    // threads and can overlap or arrive out of order, so the line carries its own count and the write is serialized.
    internal sealed class DriveOpenedReporter(Action<string> writeLine) : IProgress<IndexDriveOpened>
    {
        readonly object _gate = new();

        public void Report(IndexDriveOpened value)
        {
            lock (_gate)
            {
                writeLine($"{value.Drive}: opened ({value.SettledDriveCount} of {value.TotalDriveCount} drives)");
            }
        }
    }
}
