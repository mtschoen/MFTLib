using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Serves one drive's block scan and its journal catch-up on that drive's channel.</summary>
sealed partial class JournalBrokerHost
{
    // The processing steps a scan publishes, which a Stalled message names.
    const string RecordBatchStep = "block write";
    const string CatchUpStep = "journal catch-up";

    // Frames, in order: Cursor, ScanProgress*, ScanReady, then one terminal frame - ScanCompleted
    // with no payload when catch-up held, or CatchUpLost when it failed and the live
    // journal proves the armed cursor lost - or Error at any point. A cancelled scan (its pipe closed, or the session
    // ended) writes nothing more. The pipe publishes Queued while admission waits, WaitingOnVolume
    // around the cursor's volume open, and Processing per record batch, per flushed block range
    // and per bounded catch-up read, so a long scan keeps restarting its progress clock.
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
        channel.Pipe.Queued();
        var registration = await ParseThreads.AdmitAsync(cancellationToken).ConfigureAwait(false);
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

            await EmitScanCompletionFramesAsync(channel, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not ClientDisconnectedException)
        {
            await WriteChannelErrorAsync(channel, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<ScanOutput> ProduceBlockAsync(DriveChannel channel, BrokerFrame request,
        IBlockSectionWriter blockSectionWriter, ParseThreadAllowance parseThreads,
        CancellationToken cancellationToken)
    {
        var progressChannel = Channel.CreateBounded<BrokerScanProgress>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        var pumpTask = Task.Run(
            () => RunProgressPumpAsync(channel, progressChannel.Reader, _timeProvider,
                Hooks.ProgressThrottleInterval, cancellationToken),
            CancellationToken.None);

        try
        {
            return await Task.Run(
                () => WriteBlockAsync(channel, request, blockSectionWriter,
                    parseThreads, progressChannel.Writer, cancellationToken),
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
                progressState.Report(value, progressWriter));

            // Armed before the scan starts, so every change the scan misses is replayed by catch-up.
            Hooks.ScanStarting?.Invoke(channel.Drive, request.RetainedFileNames);
            channel.Pipe.WaitingOnVolume();
            var cursor = _volumeAccess.QueryCursor(channel.Drive);
            await channel.Pipe.WriteFrameAsync(writer => BrokerProtocol.WriteCursor(writer, cursor),
                cancellationToken).ConfigureAwait(false);

            var scanOptions = new MftScanOptions { IncludeFreed = request.IncludeFreed };
            var reporting = new BlockWriteReporting(progressReporter, channel.Pipe);
            long recordsOmitted = 0;
            var batches = PublishEachBatch(
                _volumeAccess.ScanDriveRecordBatches(channel.Drive, parseThreads, reporting, scanOptions,
                    count => recordsOmitted += count, cancellationToken),
                channel.Pipe);
            var result = blockSectionWriter.Write(request.RequireSectionName(), cursor, batches,
                new MftRowSelection(scanOptions, request.RetainedFileNames), reporting, cancellationToken);
            return progressState.Complete(cursor,
                result with { SkippedRecordCount = result.SkippedRecordCount + recordsOmitted });
        }
        finally
        {
            progressWriter.TryComplete();
        }
    }

    // Each record batch the source hands the block writer is a processing step of its own.
    static IEnumerable<IReadOnlyList<MftRecord>> PublishEachBatch(IEnumerable<IReadOnlyList<MftRecord>> batches,
        HostPipeWriter pipe)
    {
        foreach (var batch in batches)
        {
            pipe.Processing(RecordBatchStep);
            yield return batch;
        }
    }

    static async Task RunProgressPumpAsync(DriveChannel channel, ChannelReader<BrokerScanProgress> reader,
        TimeProvider timeProvider, TimeSpan progressThrottleInterval, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var lastEmit = TimeSpan.Zero;
        var first = true;
        BrokerScanProgress? throttled = null;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var progress))
                {
                    var now = timeProvider.GetElapsedTime(started);
                    if (first || now - lastEmit >= progressThrottleInterval)
                    {
                        first = false;
                        lastEmit = now;
                        throttled = null;
                        var progressToEmit = progress;
                        await channel.Pipe.WriteFrameAsync(
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
                await channel.Pipe.WriteFrameAsync(
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
            Phase = BrokerScanPhase.Transferring,
            RecordsProcessed = finalRecords,
            BytesProcessed = output.WriteResult.NamePoolUsedBytes,
            TotalRecords = finalTotalRecords,
            TotalBytes = output.WriteResult.NamePoolUsedBytes
        };

        await channel.Pipe.WriteFrameAsync(
            writer => BrokerProtocol.WriteScanProgress(writer, finalProgress),
            cancellationToken).ConfigureAwait(false);

        await channel.Pipe.WriteFrameAsync(
            writer => BrokerProtocol.WriteScanReady(writer, output.WriteResult.SkippedRecordCount),
            cancellationToken).ConfigureAwait(false);

        try
        {
            CatchUp(channel, output.Cursor, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportFailedCatchUpAsync(channel, output.Cursor, exception, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await channel.Pipe.WriteFrameAsync(
            BrokerProtocol.WriteScanCompleted,
            cancellationToken).ConfigureAwait(false);
    }

    // Reads from the armed cursor in calls of at most CatchUpBufferReadsPerCall buffers,
    // republishing progress after each advancing call, until an empty read at the tip.
    // The live watch replays these entries from the armed cursor. A throwing read or
    // entries without advancement ends catch-up; the caller classifies the journal loss.
    void CatchUp(DriveChannel channel, UsnJournalCursor armed, CancellationToken cancellationToken)
    {
        channel.Pipe.Processing(CatchUpStep);
        var cursor = armed;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (chunk, updated) = _volumeAccess.ReadJournal(channel.Drive, cursor, BrokerLiveness.CatchUpBufferReadsPerCall);
            if (updated == cursor)
            {
                if (chunk.Length != 0)
                {
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"Drive {channel.Drive} catch-up read returned {chunk.Length} entries without advancing its cursor {cursor.JournalIdentifier}:{cursor.NextUsn}."));
                }

                return;
            }

            cursor = updated;
            channel.Pipe.Processing(CatchUpStep);
        }
    }

    // The failure is never classified from the exception: the journal read throws the same
    // exception for a trimmed journal, a vanished drive and a failed read. The live journal is the
    // classifier. A proven loss of the armed cursor is CatchUpLost; a cursor still retained, or a
    // journal that cannot answer, is an ordinary drive error on this scan.
    static async Task ReportFailedCatchUpAsync(DriveChannel channel, UsnJournalCursor armed, Exception exception,
        CancellationToken cancellationToken)
    {
        var loss = JournalCheckpointCheck.Check(channel.Drive, armed.JournalIdentifier, armed.NextUsn,
            JournalCheckpointLossDetection.ScanCatchUp);
        if (loss is null)
        {
            await WriteChannelErrorAsync(channel, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await channel.Pipe.WriteFrameAsync(
            writer => BrokerProtocol.WriteCatchUpLost(writer, loss),
            cancellationToken).ConfigureAwait(false);
    }
}
