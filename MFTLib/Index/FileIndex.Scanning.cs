namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Settles every configured drive and returns once each has settled: one task per drive,
    ///     each reporting <see cref="FileIndexOptions.DriveOpenProgress" /> from its own thread when it
    ///     settles. The tasks are awaited all together, so a failure or a cancellation is thrown
    ///     only after every other drive's settle has finished, which is what lets the caller
    ///     release every block that was adopted. The configured drives are recorded before the
    ///     first task starts, so no task writes them.
    /// </summary>
    async Task SettleDrivesAsync(CancellationToken cancellationToken)
    {
        var total = _options.Drives.Count;
        var settlement = new OpenSettlement();
        var settling = new List<Task>(total);
        foreach (var drive in _options.Drives)
        {
            settling.Add(Task.Run(() => SettleDriveAsync(drive, total, settlement, cancellationToken), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(settling).ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled open reports its cancellation, whatever else a drive's settle failed with
            // while the token was being observed.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    ///     Settles one drive, then reports its settled state to
    ///     <see cref="FileIndexOptions.DriveOpenProgress" /> synchronously on the calling thread with no
    ///     lock held. The drive's <see cref="IndexDriveOpened.SettledDriveCount" /> was claimed under
    ///     <see cref="_stateLock" /> when its final state was recorded, so a callback that is slow
    ///     or blocked holds up no other drive, and reports may overlap and arrive out of count order.
    /// </summary>
    async Task SettleDriveAsync(IndexedDrive drive, int total, OpenSettlement settlement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settledCount = await GetDriveRuntime(drive.Drive)
            .AddDriveAsync(settlement, cancellationToken).ConfigureAwait(false);
        _options.DriveOpenProgress?.Report(new IndexDriveOpened(drive.Drive, settledCount, total));
    }
}
