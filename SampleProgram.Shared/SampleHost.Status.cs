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
    static RootBlockStatus RequireReadyBlock(RootStatus status)
    {
        if (status is { State: RootState.Failed, Failure: { } failure })
        {
            throw new InvalidOperationException($"The root failed ({failure.Kind}): {failure.Message}");
        }

        // An offline drive settles without a scan, so there is no catch-up to report.
        return status.Block ?? throw new InvalidOperationException("The root is offline; nothing was scanned.");
    }

    void WriteStatus(RootStatus status)
    {
        var block = RequireReadyBlock(status);
        _output.WriteLine($"Index holds {block.LiveRowCount} rows; {block.SkippedRecordCount} records skipped");
        _output.WriteLine($"producer {status.Producer?.ToString() ?? "none"}{(status.MftUnavailable is { } unavailable ? $"; MFT unavailable: {unavailable}" : string.Empty)}");
        _output.WriteLine(status.Watch.CheckpointLoss is { } loss
            ? $"Catch-up lost: {loss.Cause}"
            : $"Catch-up held; watch supported: {status.Watch.IsSupported}");
    }

    // Reports each scan phase when it starts and again each time its count advanced another ProgressRowInterval within it,
    // tracked per drive so one drive's progress never hides another's phase change or completion. The interval counts rows
    // or scanned records, never time. Reports from several drives can overlap, so the state and the write are serialized.
    internal sealed class PhaseReporter(TextWriter output) : IProgress<IndexScanProgress>
    {
        internal const uint ProgressRowInterval = 250_000;

        readonly Lock _gate = new();
        readonly Dictionary<RootKey, (IndexScanPhase Phase, uint Rows)> _lastReportByRoot = [];

        public void Report(IndexScanProgress value)
        {
            lock (_gate)
            {
                if (_lastReportByRoot.TryGetValue(value.Root, out var last)
                    && last.Phase == value.Phase
                    && value.WrittenRowCount - last.Rows < ProgressRowInterval)
                {
                    return;
                }

                _lastReportByRoot[value.Root] = (value.Phase, value.WrittenRowCount);
                output.WriteLine($"  {value.Root}: {value.Phase}{Describe(value)}");
            }
        }

        // Scanning counts records against the volume's slot count, Indexing carries no count, and the other phases count rows.
        static string Describe(IndexScanProgress value) => value.Phase switch
        {
            IndexScanPhase.Indexing => string.Empty,
            IndexScanPhase.Scanning =>
                $": {value.WrittenRowCount} records{(value.ExpectedRowCount is { } total ? $" of {total}" : string.Empty)}",
            _ => $": {value.WrittenRowCount} rows{(value.Outcome is { } outcome ? $" ({outcome})" : string.Empty)}{(value.CurrentDirectory is { } directory ? $" in {directory}" : string.Empty)}"
        };
    }
}
