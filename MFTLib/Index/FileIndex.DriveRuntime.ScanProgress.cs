namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     Produces one drive's block through <see cref="ProduceDriveBlockCoreAsync" /> and ends the
        ///     drive's scan with exactly one <see cref="IndexScanPhase.Finished" /> sample to
        ///     <see cref="FileIndexOptions.ScanProgress" />, after the producer's last sample, whether the
        ///     scan produced a block, failed, or was cancelled. Every scan entry point (open, catch-up
        ///     retry, rescan) goes through here, so each scan reports its own.
        /// </summary>
        async Task<PendingDriveResult> ProduceDriveBlockAsync(string blockPath, bool deleteOnClose,
            CancellationToken cancellationToken)
        {
            var outcome = IndexScanOutcome.Failed;
            uint rowsWritten = 0;
            try
            {
                var produced = await ProduceDriveBlockCoreAsync(blockPath, deleteOnClose, cancellationToken)
                    .ConfigureAwait(false);
                if (produced.Block is { } block)
                {
                    outcome = IndexScanOutcome.Succeeded;
                    rowsWritten = block.Header.RowCount;
                }

                return produced;
            }
            catch (OperationCanceledException)
            {
                outcome = IndexScanOutcome.Cancelled;
                throw;
            }
            finally
            {
                ReportFinished(outcome, rowsWritten);
            }
        }

        /// <summary>
        ///     Reports the Finished sample. A throwing handler is contained and logged to
        ///     <see cref="FileIndexOptions.Diagnostics" />: the scan's result is already decided, so the
        ///     handler must neither replace the original exception nor strand a produced block.
        /// </summary>
        void ReportFinished(IndexScanOutcome outcome, uint rowsWritten)
        {
            try
            {
                Options.ScanProgress?.Report(new IndexScanProgress(Drive, IndexScanPhase.Finished, rowsWritten)
                {
                    Outcome = outcome
                });
            }
            catch (Exception exception)
            {
                DiagnosticsReport.Send(Options.Diagnostics,
                    $"Drive {Drive}: the progress handler threw on the Finished sample: {exception.Message}");
            }
        }
    }
}
