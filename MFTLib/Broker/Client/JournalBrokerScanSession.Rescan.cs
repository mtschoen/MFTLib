namespace MFTLib;

public sealed partial class JournalBrokerScanSession
{
    /// <summary>
    ///     Rescan the same drives with caller-specified <paramref name="options" /> (profile,
    ///     block destinations, keepFileNames, progress) on the same elevated broker (no second UAC
    ///     prompt), replacing <see cref="LatestScan" />.
    /// </summary>
    public Task RescanAsync(
        BrokerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> drives;
        lock (_stateLock)
        {
            drives = _drives;
        }

        return RescanAsync(drives, options, cancellationToken);
    }

    /// <summary>
    ///     Rescan a different set of drives with caller-specified <paramref name="options" /> (profile, block destinations,
    ///     keepFileNames, progress) on the same broker.
    /// </summary>
    public async Task RescanAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureOperable();
        lock (_stateLock)
        {
            if (_state != JournalBrokerSessionState.Parked)
            {
                throw new InvalidOperationException("Live watch is active; call StopWatchAsync before rescanning");
            }

            if (_operationInFlight)
            {
                throw new InvalidOperationException("Another session operation is in progress");
            }

            _operationInFlight = true;
        }

        try
        {
            var transmissionStarted = false;
            BrokerScanResult result;
            try
            {
                result = await _client.ArmScanAndCatchUpAsync(
                    drives,
                    options,
                    () => transmissionStarted = true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (transmissionStarted)
            {
                // Once request transmission begins (QueryVolumes or ArmAndScan), cancellation
                // can leave broker responses unread on the pipe. Close the client before reopening
                // operations so no later request can consume frames from the cancelled exchange.
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // A Dispose or broker-death fault can land while the await above was in
            // flight; recheck under the lock and only commit the new scan if the
            // session is still operable, so a terminal state already recorded
            // elsewhere is never overwritten by a stale or incomplete rescan result.
            lock (_stateLock)
            {
                try
                {
                    EnsureOperableLocked();
                }
                catch
                {
                    // Publication is refused, so this completed result keeps LatestScan
                    // unchanged and no one else ever takes ownership of its blocks.
                    DisposeScanBlocks(result);
                    throw;
                }

                DisposeScanBlocks(_latestScan);
                _latestScan = result;
                _watchCursors = result.AdvancedCursors;
                _drives = drives;
                _profile = options.Profile;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _operationInFlight = false;
            }
        }
    }
}
