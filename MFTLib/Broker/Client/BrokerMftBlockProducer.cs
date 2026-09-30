using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Produces index blocks through a broker. The process returned by the connect callback
///     remains owned by the caller, which decides whether to share it across drives.
/// </summary>
public sealed class BrokerMftBlockProducer
{
    readonly Func<CancellationToken, Task<BrokerProcess>> _connectAsync;
    readonly BrokerScanOptions? _scanOptions;
    readonly Action<BrokerDriveScanResult>? _scanCompleted;

    /// <summary>Builds a producer over the given broker connection and scan defaults.</summary>
    /// <param name="connectAsync">
    ///     Yields the process each scan runs on. The caller keeps ownership of what it returns.
    /// </param>
    /// <param name="scanOptions">Base options for every scan: profile, keep-file names and progress.</param>
    /// <param name="scanCompleted">
    ///     Invoked with the scan result once it has passed validation, and not at all when the
    ///     scan failed or the block was rejected. The callback must not retain the result's
    ///     block: ownership passes to the index as soon as this producer returns, and the
    ///     producer disposes it on every failure path.
    /// </param>
    public BrokerMftBlockProducer(
        Func<CancellationToken, Task<BrokerProcess>> connectAsync,
        BrokerScanOptions? scanOptions = null,
        Action<BrokerDriveScanResult>? scanCompleted = null)
    {
        _connectAsync = connectAsync;
        _scanOptions = scanOptions;
        _scanCompleted = scanCompleted;
    }

    public MftBlockProducer CreateProducer() => ProduceAsync;

    /// <summary>The watch source over the same broker connection: each drive's watch runs on a pipe of its own.</summary>
    public IIndexWatchSource CreateWatchSource() => new BrokerIndexWatchSource(_connectAsync);

    // A result carrying a proven catch-up loss still transfers its block: the scan is complete,
    // and the loss travels with it so the index can refuse to watch from the block's cursor.
    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var options = (_scanOptions ?? new BrokerScanOptions()) with
        {
            Progress = BrokerProgressAdapter.Create(request, _scanOptions?.Progress)
        };
        var target = new BlockScanTarget(request.BlockPath, request.VolumeSerial, request.DeleteOnClose)
        {
            CacheTag = request.CacheTag
        };
        var process = await _connectAsync(cancellationToken).ConfigureAwait(false);
        var result = await process.ScanDriveAsync(request.DriveLetter, target, options, cancellationToken)
            .ConfigureAwait(false);
        var block = result.Block.Block;
        try
        {
            ValidateBlock(block, request.VolumeSerial, result.ArmedCursor, request.CacheTag);
            _scanCompleted?.Invoke(result);
            return new MftBlockProduceResult(block, result.ArmedCursor.JournalId, result.ArmedCursor.NextUsn,
                SkippedRecordCount: checked((int)result.Block.SkippedRecordCount),
                CompactionNeeded: block.Header.IsCompactionNeeded)
            {
                CatchUpLoss = result.CatchUpLoss
            };
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    static void ValidateBlock(BlockFile block, uint volumeSerial, UsnJournalCursor armed, CacheTag requestedCacheTag)
    {
        var header = block.Header;
        if (header.RowCount == 0)
        {
            throw new InvalidOperationException("Block RowCount must be greater than zero.");
        }

        var validation = BlockHeader.Validate(in header, volumeSerial, block.Length);
        if (validation != BlockValidationResult.Valid)
        {
            throw new InvalidOperationException($"Block validation failed: {validation}.");
        }

        if (header.ProducerKind != ProducerKind.Mft)
        {
            throw new InvalidOperationException("Block ProducerKind must be Mft.");
        }

        if (header.UsnJournalId != armed.JournalId || header.UsnNextUsn != armed.NextUsn)
        {
            throw new InvalidOperationException("Block journal cursor does not match the armed cursor.");
        }

        // Checked here, before the validated-result callback: a block that carries a different
        // tag than requested is a producer failure, exactly like a wrong journal cursor above,
        // and must not be handed to the callback or returned as adopted.
        if (header.CacheTag != requestedCacheTag)
        {
            throw new InvalidOperationException(
                $"Block cache tag {header.CacheTag} does not match the requested tag {requestedCacheTag}.");
        }
    }
}
