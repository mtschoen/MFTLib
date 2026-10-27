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
        _output.WriteLine($"Index holds {block.LiveRowCount} rows; {block.SkippedRecordCount} records skipped");
        _output.WriteLine(status.Watch.CheckpointLoss is { } loss
            ? $"Catch-up lost: {loss.Cause}"
            : $"Catch-up held; watch supported: {status.Watch.IsSupported}");
    }

    // Reports each scan phase when it starts and again each time another ProgressRowInterval rows were written within it,
    // tracked per drive so one drive's progress never hides another's phase change or completion. The interval counts rows,
    // never time. Reports from several drives can overlap, so the state and the write are serialized.
    internal sealed class PhaseReporter(TextWriter output) : IProgress<IndexScanProgress>
    {
        internal const uint ProgressRowInterval = 250_000;

        readonly Lock _gate = new();
        readonly Dictionary<DriveKey, (IndexScanPhase Phase, uint Rows)> _lastReportByDrive = [];

        public void Report(IndexScanProgress value)
        {
            lock (_gate)
            {
                if (_lastReportByDrive.TryGetValue(value.Drive, out var last)
                    && last.Phase == value.Phase
                    && value.WrittenRowCount - last.Rows < ProgressRowInterval)
                {
                    return;
                }

                _lastReportByDrive[value.Drive] = (value.Phase, value.WrittenRowCount);
                output.WriteLine($"  {value.Drive}: {value.Phase}: {value.WrittenRowCount} rows{(value.ExpectedRowCount is { } total ? $" of {total}" : string.Empty)}{(value.Outcome is { } outcome ? $" ({outcome})" : string.Empty)}{(value.CurrentDirectory is { } directory ? $" in {directory}" : string.Empty)}");
            }
        }
    }
}
