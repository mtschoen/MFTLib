namespace MFTLib;

public sealed partial class JournalBrokerScanSession
{
    /// <summary>
    ///     Rescan the same drives, profile, and <c>keepFileNames</c> the session was
    ///     started or last rescanned with, on the same elevated broker (no second UAC
    ///     prompt), replacing <see cref="LatestScan" />. Legal only in
    ///     <see cref="JournalBrokerSessionState.Parked" />; call <see cref="StopWatchAsync" />
    ///     first if watching. Throws <see cref="InvalidOperationException" /> if the broker
    ///     dies during the rescan.
    /// </summary>
    public Task RescanAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> drives;
        BrokerScanProfile profile;
        IReadOnlyCollection<string>? keepFileNames;
        lock (_stateLock)
        {
            drives = _drives;
            profile = _profile;
            keepFileNames = _keepFileNames;
        }

        return RescanAsync(drives, new BrokerScanOptions
        {
            Profile = profile,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    /// <summary>
    ///     Rescan the same drives with caller-specified <paramref name="options" /> (profile,
    ///     consumer, keepFileNames, progress) on the same elevated broker (no second UAC
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
    ///     Rescan the same drives, profile, and <c>keepFileNames</c> the session was
    ///     started or last rescanned with, streaming records to <paramref name="consumeRecords" />
    ///     on the same elevated broker (no second UAC prompt), replacing <see cref="LatestScan" />.
    /// </summary>
    public Task RescanAsync(
        ScanRecordBatchConsumer consumeRecords,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> drives;
        BrokerScanProfile profile;
        IReadOnlyCollection<string>? keepFileNames;
        lock (_stateLock)
        {
            drives = _drives;
            profile = _profile;
            keepFileNames = _keepFileNames;
        }

        return RescanAsync(drives, new BrokerScanOptions
        {
            Profile = profile,
            ConsumeRecords = consumeRecords,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    /// <summary>Rescan a different set of drives (same profile and keepFileNames) on the same broker.</summary>
    public Task RescanAsync(IReadOnlyList<string> drives, CancellationToken cancellationToken = default)
    {
        BrokerScanProfile profile;
        IReadOnlyCollection<string>? keepFileNames;
        lock (_stateLock)
        {
            profile = _profile;
            keepFileNames = _keepFileNames;
        }

        return RescanAsync(drives, new BrokerScanOptions
        {
            Profile = profile,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    /// <summary>
    ///     Rescan a different set of drives (same profile and keepFileNames) on the same broker, streaming records to
    ///     <paramref name="consumeRecords" />.
    /// </summary>
    public Task RescanAsync(
        IReadOnlyList<string> drives,
        ScanRecordBatchConsumer consumeRecords,
        CancellationToken cancellationToken = default)
    {
        BrokerScanProfile profile;
        IReadOnlyCollection<string>? keepFileNames;
        lock (_stateLock)
        {
            profile = _profile;
            keepFileNames = _keepFileNames;
        }

        return RescanAsync(drives, new BrokerScanOptions
        {
            Profile = profile,
            ConsumeRecords = consumeRecords,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    /// <summary>Rescan a different set of drives with a different profile and keepFileNames on the same broker.</summary>
    public Task RescanAsync(
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        return RescanAsync(drives, new BrokerScanOptions
        {
            Profile = profile,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    /// <summary>
    ///     Rescan a different set of drives with a different profile and keepFileNames on the same broker, streaming
    ///     records to <paramref name="consumeRecords" />.
    /// </summary>
    public Task RescanAsync(
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        ScanRecordBatchConsumer consumeRecords,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        return RescanAsync(drives, new BrokerScanOptions
        {
            Profile = profile,
            ConsumeRecords = consumeRecords,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    /// <summary>
    ///     Rescan a different set of drives with caller-specified <paramref name="options" /> (profile, consumer,
    ///     keepFileNames, progress) on the same broker.
    /// </summary>
    public async Task RescanAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions? options,
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
            var profile = options?.Profile ?? BrokerScanProfile.Full;
            var keepFileNames = options?.KeepFileNames;
            lock (_stateLock)
            {
                EnsureOperableLocked();
                _latestScan = result;
                _watchCursors = result.AdvancedCursors;
                _drives = drives;
                _profile = profile;
                _keepFileNames = keepFileNames;
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
