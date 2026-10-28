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
            await Task.Run(() => _view.Scan.AddRootAsync(cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled open reports its cancellation, whatever else the settle failed with
            // while the token was being observed.
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
