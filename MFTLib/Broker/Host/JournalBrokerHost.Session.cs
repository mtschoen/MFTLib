namespace MFTLib;

public sealed partial class JournalBrokerHost
{
    /// <summary>
    ///     Serve a broker session over <paramref name="stream" /> (the pipe). Reads
    ///     request frames in a loop. On <see cref="BrokerFrameKind.ArmAndScan" />, for
    ///     each drive: arm the cursor, emit a <c>Cursor</c> frame, write packed index rows via
    ///     <paramref name="blockSectionWriter" /> into the caller-created map, emit
    ///     <c>ScanReady</c>, run catch-up, emit a <c>JournalBatch</c>. Per-drive
    ///     failures emit an <c>Error</c> frame and continue (non-fatal contract). On
    ///     <see cref="BrokerFrameKind.QueryVolumes" />, answers one <c>VolumeInfo</c> (or
    ///     <c>Error</c>) frame per drive without arming a scan, then keeps serving - the
    ///     caller can follow it with <c>ArmAndScan</c> on the same connection.
    ///     <c>StartWatch</c> arms or re-arms each drive it names, and
    ///     <c>DisarmDrive</c> retires one drive while the others keep streaming. A
    ///     per-drive watch failure ends that drive's stream with an <c>Error</c> frame.
    ///     Each <c>StartWatch</c> token carries its drive's client-issued arm epoch.
    ///     Every live <c>JournalBatch</c> and <c>Error</c> echoes that epoch so the client
    ///     can distinguish the current arm's frames from frames an earlier arm produced.
    ///     Returns on <c>Shutdown</c>, on EOF, or after one arm-and-scan when
    ///     <paramref name="oneShot" /> is set (a single-UAC CLI-style path).
    /// </summary>
    /// <param name="stream">The connected pipe to serve.</param>
    /// <param name="blockSectionWriter">
    ///     Writes packed rows into the client-created section. Null serves a watch-only
    ///     session; an <c>ArmAndScan</c> on such a session then fails with one <c>Error</c>
    ///     frame per requested drive rather than one argument failure up front.
    /// </param>
    /// <param name="oneShot">Return after a single arm-and-scan instead of serving on.</param>
    /// <param name="cancellationToken">Stops serving.</param>
    public async Task ServeAsync(Stream stream, IBlockSectionWriter? blockSectionWriter, bool oneShot,
        CancellationToken cancellationToken)
    {
        // The pipe is shared by concurrent watch tasks; guard writes so frames
        // never interleave on the wire.
        using var writeLock = new SemaphoreSlim(1, 1);
        var watch = new WatchGeneration();
        try
        {
            await ServeFramesAsync(stream, blockSectionWriter, oneShot, writeLock, watch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal shutdown signal for a live watch session.
        }
        finally
        {
            await StopWatchGenerationAsync(watch).ConfigureAwait(false);
        }
    }

    async Task ServeFramesAsync(
        Stream stream,
        IBlockSectionWriter? blockSectionWriter,
        bool oneShot,
        SemaphoreSlim writeLock,
        WatchGeneration watch,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                return;
            }

            switch (frame.Value.Kind)
            {
                case BrokerFrameKind.ArmAndScan:
                    if (frame.Value.DrivesSpec is { } drivesSpec)
                    {
                        await HandleArmAndScanAsync(stream, blockSectionWriter, drivesSpec, frame.Value.KeepFileNames,
                            writeLock, cancellationToken).ConfigureAwait(false);
                    }

                    if (oneShot)
                    {
                        return;
                    }

                    break;

                case BrokerFrameKind.QueryVolumes:
                    if (frame.Value.DrivesSpec is { } queryVolumesSpec)
                    {
                        await HandleQueryVolumesAsync(stream, queryVolumesSpec, writeLock, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case BrokerFrameKind.StartWatch:
                    if (frame.Value.DrivesSpec is { } watchSpec)
                    {
                        await ArmWatchDrivesAsync(stream, writeLock, watch, watchSpec, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case BrokerFrameKind.DisarmDrive:
                    await DisarmWatchDriveAsync(watch, NormalizeWatchDrive(frame.Value.RequireDrive())).ConfigureAwait(false);
                    break;

                case BrokerFrameKind.EndWatch:
                    await StopWatchGenerationAsync(watch).ConfigureAwait(false);
                    await WriteFrameAsync(stream, writeLock, BrokerProtocol.WriteEndWatchAck, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case BrokerFrameKind.Shutdown:
                    return;
            }
        }
    }

    async Task ArmWatchDrivesAsync(
        Stream stream,
        SemaphoreSlim writeLock,
        WatchGeneration watch,
        string watchSpec,
        CancellationToken cancellationToken)
    {
        // Arming a drive that is already armed stops its task and awaits it to a stop
        // before the fresh one starts, so one drive never has two tasks writing frames at
        // once. That is what replaces the old refusal to act on a second StartWatch: the
        // refusal existed only because the frame loop had no way to retire a running
        // generation safely, and awaiting one drive to a stop is that way. Drives this
        // spec does not name are not touched.
        watch.Cancellation ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var request in ParseWatchSpec(watchSpec))
        {
            await DisarmWatchDriveAsync(watch, request.Letter).ConfigureAwait(false);
            watch.DriveWatches[request.Letter] = new DriveWatch(
                driveCancellationToken => StreamWatchAsync(stream, request, writeLock, driveCancellationToken),
                watch.Cancellation.Token);
        }
    }

    // Cancel one drive's task, await its quiescence, and forget it. StreamWatchAsync
    // catches OperationCanceledException internally and always returns normally, so this
    // await cannot fault. A drive that is not armed is not an error: a client may disarm
    // a drive whose stream the host already ended with its Error frame.
    static async Task DisarmWatchDriveAsync(WatchGeneration watch, string drive)
    {
        if (!watch.DriveWatches.Remove(drive, out var driveWatch))
        {
            return;
        }

        await driveWatch.Cancellation.CancelAsync().ConfigureAwait(false);
        await driveWatch.Task.ConfigureAwait(false);
        driveWatch.Dispose();
    }

    static async Task StopWatchGenerationAsync(WatchGeneration watch)
    {
        if (watch.Cancellation == null)
        {
            return;
        }

        await watch.Cancellation.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(watch.DriveWatches.Values.Select(driveWatch => driveWatch.Task)).ConfigureAwait(false);
        foreach (var driveWatch in watch.DriveWatches.Values)
        {
            driveWatch.Dispose();
        }

        watch.DriveWatches.Clear();
        watch.Cancellation.Dispose();
        watch.Cancellation = null;
    }

    sealed class WatchGeneration
    {
        public readonly Dictionary<string, DriveWatch> DriveWatches = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource? Cancellation;
    }

    sealed class DriveWatch : IDisposable
    {
        public DriveWatch(Func<CancellationToken, Task> startWatch,
            CancellationToken generationCancellationToken)
        {
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(generationCancellationToken);
            Task = startWatch(Cancellation.Token);
        }

        public CancellationTokenSource Cancellation { get; }

        public Task Task { get; }

        public void Dispose()
        {
            Cancellation.Dispose();
        }
    }
}
