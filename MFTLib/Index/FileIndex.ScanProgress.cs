namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Produces one drive's block through <see cref="ProduceDriveBlockCoreAsync" /> and ends the
    ///     drive's scan with exactly one <see cref="IndexScanPhase.Finished" /> sample to
    ///     <see cref="FileIndexOptions.Progress" />, after the producer's last sample, whether the
    ///     scan produced a block, failed, or was cancelled. Every scan entry point (open, catch-up
    ///     retry, rescan) goes through here, so each scan reports its own.
    /// </summary>
    async Task<PendingDriveResult> ProduceDriveBlockAsync(IndexedDrive drive, string blockPath, bool deleteOnClose,
        CancellationToken cancellationToken)
    {
        var outcome = IndexScanOutcome.Failed;
        uint rowsWritten = 0;
        try
        {
            var produced = await ProduceDriveBlockCoreAsync(drive, blockPath, deleteOnClose, cancellationToken)
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
            ReportFinished(drive.DriveLetter, outcome, rowsWritten);
        }
    }

    /// <summary>
    ///     Reports the Finished sample. A throwing handler is contained and logged to
    ///     <see cref="FileIndexOptions.Diagnostics" />: the scan's result is already decided, so the
    ///     handler must neither replace the original exception nor strand a produced block.
    /// </summary>
    void ReportFinished(char driveLetter, IndexScanOutcome outcome, uint rowsWritten)
    {
        try
        {
            _options.Progress?.Report(new IndexScanProgress
            {
                DriveLetter = driveLetter,
                Phase = IndexScanPhase.Finished,
                RowsWritten = rowsWritten,
                Outcome = outcome
            });
        }
        catch (Exception exception)
        {
            try
            {
                _options.Diagnostics?.Invoke(
                    $"Drive {driveLetter}: the progress handler threw on the Finished sample: {exception.Message}");
            }
            catch (Exception diagnosticsException) when (diagnosticsException is not OutOfMemoryException)
            {
                // The callback is the consumer's; its failure must not replace the scan's own result.
            }
        }
    }
}
