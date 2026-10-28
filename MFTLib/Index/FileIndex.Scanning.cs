namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Settles the configured root and returns once it has settled.
    /// </summary>
    async Task SettleRootAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _view.Scan.SettleViewAsync(_view, cancellationToken).ConfigureAwait(false) is { } reason)
            {
                await _view.DetachAsync().ConfigureAwait(false);
                _view.Resolved = _view.Resolved.AsWalk(reason);
                _view.Scan = new VolumeScan(new VolumeScanCreation(_view.Resolved, _options, CacheDirectoryPath, Hooks));
                _ = await _view.Scan.SettleViewAsync(_view, cancellationToken).ConfigureAwait(false);
            }
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled open reports its cancellation, whatever else the settle failed with
            // while the token was being observed.
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
