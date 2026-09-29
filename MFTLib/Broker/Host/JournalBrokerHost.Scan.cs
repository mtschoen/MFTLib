using System.Diagnostics;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Serves one drive's block scan and its journal catch-up on that drive's channel.</summary>
public sealed partial class JournalBrokerHost
{
    // Frames, in order: Cursor, ScanProgress*, ScanReady, then one terminal frame - JournalBatch
    // when catch-up held, or CatchUpLost when it failed and the live journal proves the armed
    // cursor lost - or Error at any point. A cancelled scan (its pipe closed, or the session
    // ended) writes nothing more.
    async Task RunScanAsync(DriveChannel channel, BrokerFrame request, IBlockSectionWriter? blockSectionWriter,
        CancellationToken cancellationToken)
    {
        if (blockSectionWriter == null)
        {
            await WriteChannelErrorAsync(channel, "Block scans require the blockSectionWriter session parameter.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // A closed pipe cancels the wait and takes the scan out of the queue; its source never runs.
        channel.Operation.Queued();
        var registration = await _parseThreads.AdmitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ScanOutput output;
            // The scan's share of the budget is returned only once the pipeline below has
            // returned, and the pipeline returns only after the native parse has: the volume and
            // the section are released inside it, then the registration here.
            using (registration)
            {
                output = await ProduceBlockAsync(channel, request, blockSectionWriter, registration.Allowance,
                    cancellationToken).ConfigureAwait(false);
            }

            await EmitScanCompletionFramesAsync(channel, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not ClientDisconnectedException)
        {
            await WriteChannelErrorAsync(channel, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<ScanOutput> ProduceBlockAsync(DriveChannel channel, BrokerFrame request,
        IBlockSectionWriter blockSectionWriter, ParseThreadAllowance parseThreads, CancellationToken cancellationToken)
    {
        var progressChannel = Channel.CreateBounded<BrokerScanProgress>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        var pumpTask = Task.Run(
            () => RunProgressPumpAsync(channel, progressChannel.Reader, cancellationToken),
            CancellationToken.None);

        try
        {
            return await Task.Run(
                () => WriteBlockAsync(channel, request, blockSectionWriter, parseThreads, progressChannel.Writer,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        // The pump is awaited before the completion frames are written. A client that disappeared
        // mid-scan is usually noticed by one of the pump's progress writes, and the
        // ClientDisconnectedException that write throws surfaces through this await, so the
        // channel ends here instead of writing completion frames nobody can receive.
        finally
        {
            await pumpTask.ConfigureAwait(false);
        }
    }

    async Task<ScanOutput> WriteBlockAsync(DriveChannel channel, BrokerFrame request,
        IBlockSectionWriter blockSectionWriter, ParseThreadAllowance parseThreads,
        ChannelWriter<BrokerScanProgress> progressWriter, CancellationToken cancellationToken)
    {
        try
        {
            var progressState = new ScanProgressState();
            var progressReporter = new DirectProgress<BlockWriteProgress>(value =>
                progressState.Report(channel.Drive, value, progressWriter));

            // Armed before the scan starts, so every change the scan misses is replayed by catch-up.
            var cursor = _queryCursor(channel.Drive);
            await WriteChannelFrameAsync(channel, writer => BrokerProtocol.WriteCursor(writer, cursor),
                cancellationToken).ConfigureAwait(false);

            var batches = _scanDrive(channel.Drive, parseThreads, channel.Operation, progressReporter,
                cancellationToken);
            var filter = new MftBlockRowFilter(request.Profile, request.KeepFileNames);
            var result = blockSectionWriter.Write(request.RequireSectionName(), cursor, batches, filter,
                progressReporter, cancellationToken);
            return progressState.Complete(cursor, result);
        }
        finally
        {
            progressWriter.TryComplete();
        }
    }

    static async Task RunProgressPumpAsync(DriveChannel channel, ChannelReader<BrokerScanProgress> reader,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastEmit = TimeSpan.Zero;
        var first = true;
        BrokerScanProgress? throttled = null;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var progress))
                {
                    var now = stopwatch.Elapsed;
                    if (first || now - lastEmit >= _progressThrottleInterval)
                    {
                        first = false;
                        lastEmit = now;
                        throttled = null;
                        var progressToEmit = progress;
                        await WriteChannelFrameAsync(channel,
                            writer => BrokerProtocol.WriteScanProgress(writer, progressToEmit),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throttled = progress;
                    }
                }
            }

            // The channel completed normally: flush the newest report the throttle window held
            // back (typically the parse phase's records == total report), so the last
            // pre-completion value reaches the client instead of being dropped. Cancellation must
            // not reach this flush - a cancelled scan emits no partial final frame.
            if (throttled is { } pending)
            {
                await WriteChannelFrameAsync(channel,
                    writer => BrokerProtocol.WriteScanProgress(writer, pending),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // aislop-ignore-next-line SwallowedException -- intentional clean shutdown of progress pump when scan completes or is cancelled
        }
    }

    async Task EmitScanCompletionFramesAsync(DriveChannel channel, ScanOutput output,
        CancellationToken cancellationToken)
    {
        var finalRecords = output.TotalRecords ?? output.WriteResult.RowCount;
        if (finalRecords < output.MaximumRecordsProcessed)
        {
            finalRecords = output.MaximumRecordsProcessed;
        }

        long? finalTotalRecords = output.TotalRecords ?? output.WriteResult.RowCount;
        if (finalTotalRecords.Value < finalRecords)
        {
            finalTotalRecords = finalRecords;
        }

        var finalProgress = new BrokerScanProgress
        {
            DriveLetter = channel.Drive,
            Phase = BrokerScanPhase.Transferring,
            RecordsProcessed = finalRecords,
            BytesProcessed = output.WriteResult.NamePoolUsedBytes,
            TotalRecords = finalTotalRecords,
            TotalBytes = output.WriteResult.NamePoolUsedBytes,
            Elapsed = output.Elapsed
        };

        await WriteChannelFrameAsync(channel,
            writer => BrokerProtocol.WriteScanProgress(writer, finalProgress),
            cancellationToken).ConfigureAwait(false);

        await WriteChannelFrameAsync(channel,
            writer => BrokerProtocol.WriteScanReady(writer, output.WriteResult.RowCount,
                output.WriteResult.NamePoolUsedBytes, output.WriteResult.SkippedRecordCount),
            cancellationToken).ConfigureAwait(false);

        UsnJournalEntry[] entries;
        UsnJournalCursor updated;
        try
        {
            (entries, updated) = CatchUp(channel.Drive, output.Cursor);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportFailedCatchUpAsync(channel, output.Cursor, exception, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // The terminal catch-up batch always ships - the client's scan collector waits on it to
        // complete the drive - but the diagnostics logs' own entries are still filtered out, so a
        // scan that raced a busy diagnostics log does not replay them.
        var logFilter = BrokerDiagnostics.CreateLogFilter();
        if (logFilter != null)
        {
            entries = logFilter.Filter(channel.Drive, entries);
        }

        await WriteChannelFrameAsync(channel,
            writer => BrokerProtocol.WriteJournalBatch(writer, updated, entries),
            cancellationToken).ConfigureAwait(false);
    }

    // The failure is never classified from the exception: the journal read throws the same
    // exception for a trimmed journal, a vanished drive and a failed read. The live journal is the
    // classifier. A proven loss of the armed cursor is CatchUpLost; a cursor still retained, or a
    // journal that cannot answer, is an ordinary drive error on this scan.
    static async Task ReportFailedCatchUpAsync(DriveChannel channel, UsnJournalCursor armed, Exception exception,
        CancellationToken cancellationToken)
    {
        var loss = JournalCheckpointCheck.Check(channel.Drive[0], armed.JournalId, armed.NextUsn,
            JournalCheckpointLossDetection.ScanCatchUp);
        if (loss is null)
        {
            await WriteChannelErrorAsync(channel, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteChannelFrameAsync(channel,
            writer => BrokerProtocol.WriteCatchUpLost(writer, loss, exception.Message),
            cancellationToken).ConfigureAwait(false);
    }
}
