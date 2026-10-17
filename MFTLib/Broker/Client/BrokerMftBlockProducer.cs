using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Scans and watches drives through a broker. The process returned by the connect callback
///     remains owned by the caller, which decides whether to share it across drives.
/// </summary>
internal sealed class BrokerMftBlockProducer
{
    readonly Func<CancellationToken, Task<BrokerProcess>> _connectAsync;
    readonly MftScanOptions? _scanOptions;

    /// <summary>Builds a producer over the given broker connection and scan defaults.</summary>
    /// <param name="connectAsync">
    ///     Yields the process each scan runs on. The caller keeps ownership of what it returns.
    /// </param>
    /// <param name="scanOptions">Base options for every scan: retention, freed-record inclusion and progress.</param>
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
    ///     <see cref="FileIndexOptions.MftSource" />. Each drive scan runs on a broker obtained from
    ///     the connect function, and each drive's watch runs on a pipe of its own.
    /// </summary>
    /// <returns>A source whose scans fill the block named by each request through the broker.</returns>
    internal MftIndexSource CreateIndexSource() => new(ProduceAsync, new BrokerIndexWatchSource(_connectAsync));

    // A result carrying a proven catch-up loss still transfers its block: the scan is complete,
    // and the loss travels with it so the index can refuse to watch from the block's cursor.
    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var options = _scanOptions ?? new MftScanOptions();
        var progress = MftBlockProgressAdapter.Create(request);
        var target = new BlockScanTarget(request.BlockPath, request.VolumeSerial, request.DeleteOnClose)
        {
            CacheTag = request.CacheTag
        };
        var process = await _connectAsync(cancellationToken).ConfigureAwait(false);
        var result = await process.ScanDriveAsync(request.Drive.Letter, target, options, progress, cancellationToken)
            .ConfigureAwait(false);
        return MftBlockValidation.Finish(request, result.Block.Block, result.ArmedCursor,
            MftBlockScan.SaturateSkippedRecordCount(result.Block.SkippedRecordCount), result.CatchUpLoss);
    }
}
