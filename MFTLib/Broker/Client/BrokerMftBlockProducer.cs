using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Scans and watches volumes through a broker. The process returned by the connect callback
///     remains owned by the caller, which decides whether to share it across volumes.
/// </summary>
sealed class BrokerMftBlockProducer
{
    readonly Func<CancellationToken, Task<BrokerProcess>> _connectAsync;
    readonly MftScanOptions? _scanOptions;

    /// <summary>Builds a producer over the given broker connection and scan defaults.</summary>
    /// <param name="connectAsync">
    ///     Yields the process each scan runs on. The caller keeps ownership of what it returns.
    /// </param>
    /// <param name="scanOptions">Base options for every scan: freed-record inclusion and progress.</param>
    internal BrokerMftBlockProducer(
        Func<CancellationToken, Task<BrokerProcess>> connectAsync,
        MftScanOptions? scanOptions = null)
    {
        ArgumentNullException.ThrowIfNull(connectAsync);
        _connectAsync = connectAsync;
        _scanOptions = scanOptions;
    }

    /// <summary>
    ///     The index source over this broker connection, to assign to
    ///     <see cref="FileIndexOptions.MftSource" />. Each volume scan runs on a broker obtained from
    ///     the connect function, and each volume's watch runs on a pipe of its own.
    /// </summary>
    /// <returns>A source whose scans fill the block named by each request through the broker.</returns>
    internal MftIndexSource CreateIndexSource() => new(ProduceAsync, new BrokerIndexWatchSource(_connectAsync),
        includeFreed: _scanOptions?.IncludeFreed ?? false);

    // A result carrying a proven catch-up loss still transfers its block: the scan is complete,
    // and the loss travels with it so the index can refuse to watch from the block's cursor.
    Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken) =>
        MftBlockGrowth.ProduceUntilFitAsync(request, grown => ScanAsync(grown, cancellationToken));

    async Task<MftBlockProduceResult> ScanAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var options = _scanOptions ?? new MftScanOptions();
        var progress = MftBlockProgressAdapter.Create(request);
        var target = new BlockScanTarget(request.BlockPath, request.VolumeSerial, request.DeleteOnClose)
        {
            CacheTag = request.CacheTag,
            ScanShape = request.ScanShape,
            MinimumCapacities = request.MinimumCapacities
        };
        var process = await ConnectAsMftAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        var result = await process.ScanVolumeAsync(request.Volume, target, options, progress, cancellationToken)
            .ConfigureAwait(false);
        return MftBlockValidation.Finish(request, result.Block.Block, result.ArmedCursor,
            MftBlockScan.SaturateSkippedRecordCount(result.Block.SkippedRecordCount), result.CatchUpLoss);
    }

    // Only the connect is translated: a broker that was never reached (declined elevation, a connect timeout, a
    // session that already ended) is an eligibility reason, and an Automatic open walks. Everything after the connect,
    // and every other connect failure, is the producer failing and propagates unchanged.
    async Task<BrokerProcess> ConnectAsMftAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _connectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerLaunchDeclinedException exception)
        {
            throw new MftUnavailableException(MftUnavailableReason.ElevationDeclined, exception.Message, exception);
        }
        catch (Exception exception) when (exception is TimeoutException or BrokerEndedException)
        {
            throw new MftUnavailableException(MftUnavailableReason.BrokerUnavailable, exception.Message, exception);
        }
    }
}
