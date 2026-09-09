namespace MFTLib;

/// <summary>Arms a drive's journal cursor before writing its record batches into a client-created block.</summary>
public sealed partial class JournalBrokerHost
{
    async Task<(UsnJournalCursor cursor, BlockWriteResult writeResult)> ExecuteBlockScanAsync(
        ScanDriveInput input, IBlockSectionWriter? blockSectionWriter, IProgress<BlockWriteProgress> progressReporter,
        Func<UsnJournalCursor, Task> emitCursorAsync, CancellationToken cancellationToken)
    {
        if (blockSectionWriter == null)
        {
            throw new InvalidOperationException("Block scans require the blockSectionWriter session parameter.");
        }

        var cursor = _queryCursor(input.Request.Letter);
        var batches = _scanDrive(input.Request.Letter, progressReporter, cancellationToken);
        await emitCursorAsync(cursor).ConfigureAwait(false);
        var filter = new MftBlockRowFilter(input.Request.Profile, input.KeepFileNames);
        var result = blockSectionWriter.Write(
            input.Request.MmfName, cursor, batches, filter, progressReporter, cancellationToken);
        return (cursor, result);
    }
}
