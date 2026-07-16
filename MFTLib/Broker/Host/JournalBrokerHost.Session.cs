namespace MFTLib;

public sealed partial class JournalBrokerHost
{
    /// <summary>
    /// Serve a broker session over <paramref name="stream"/> (the pipe). Reads
    /// request frames in a loop. On <see cref="BrokerFrameKind.ArmAndScan"/>, for
    /// each drive: arm the cursor, emit a <c>Cursor</c> frame, write the scan
    /// payload into the caller-created map via <paramref name="mmfWriter"/>, emit
    /// <c>ScanReady</c>, run catch-up, emit a <c>JournalBatch</c>. Per-drive
    /// failures emit an <c>Error</c> frame and continue (non-fatal contract).
    /// Returns on <c>Shutdown</c>, on EOF, or after one arm-and-scan when
    /// <paramref name="oneShot"/> is set (a single-UAC CLI-style path).
    /// </summary>
    public async Task ServeAsync(Stream stream, IMmfWriter mmfWriter, bool oneShot, CancellationToken cancellationToken)
    {
        // The pipe is shared by concurrent watch tasks; guard writes so frames
        // never interleave on the wire.
        var writeLock = new SemaphoreSlim(1, 1);
        // The current watch generation: its CTS and the per-drive tasks it owns. A
        // StartWatch creates a generation; an EndWatch (or loop end) tears it down.
        // Held in a container (rather than reassigned locals) so the local async
        // function below captures a stable reference instead of a modified closure
        // (ref params are illegal in async methods).
        var watch = new WatchGeneration();

        // Cancel the current watch generation, await its tasks to quiescence, and
        // clear it. StreamWatchAsync catches OperationCanceledException internally
        // and always returns normally, so Task.WhenAll here cannot fault.
        async Task StopWatchGenerationAsync()
        {
            if (watch.Cancellation == null)
                return;
            await watch.Cancellation.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(watch.Tasks).ConfigureAwait(false);
            watch.Tasks.Clear();
            watch.Cancellation.Dispose();
            watch.Cancellation = null;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                if (frame == null)
                    return; // EOF / pipe closed

                switch (frame.Value.Kind)
                {
                    case BrokerFrameKind.ArmAndScan:
                        if (frame.Value.DrivesSpec is { } drivesSpec)
                            await HandleArmAndScanAsync(stream, mmfWriter, drivesSpec, frame.Value.KeepFileNames,
                                writeLock, cancellationToken)
                                .ConfigureAwait(false);
                        if (oneShot)
                            return;
                        break;

                    case BrokerFrameKind.StartWatch:
                        if (frame.Value.DrivesSpec is { } watchSpec)
                        {
                            // Idempotent: if a watch generation is already live (no EndWatch
                            // arrived to retire it), a second StartWatch is a duplicate. DO NOT
                            // restart it - tearing the running generation down mid-write races
                            // its in-flight frames against the new generation's and desyncs the
                            // caller's single-reader demux (observed as "Unknown frame kind" on a
                            // warm start). A real restart always sends EndWatch first, which
                            // clears watch.Cancellation back to null.
                            if (watch.Cancellation != null)
                            {
                                BrokerDiagnostics.Log(
                                    "StartWatch ignored: a watch generation is already running " +
                                    "(duplicate StartWatch without an intervening EndWatch).");
                                break;
                            }
                            watch.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            watch.Tasks.AddRange(StartWatchTasks(stream, watchSpec, writeLock, watch.Cancellation.Token));
                        }
                        break;

                    case BrokerFrameKind.EndWatch:
                        await StopWatchGenerationAsync().ConfigureAwait(false);
                        await WriteFrameAsync(stream, writeLock, BrokerProtocol.WriteEndWatchAck, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case BrokerFrameKind.Shutdown:
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal shutdown signal for a live watch session.
        }
        finally
        {
            await StopWatchGenerationAsync().ConfigureAwait(false);
        }
    }

}
