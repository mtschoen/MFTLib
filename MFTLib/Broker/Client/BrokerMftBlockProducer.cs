using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Produces index blocks through a broker. Clients returned by the connect callback remain
///     owned by the caller, which decides whether to share a client across drives.
/// </summary>
public sealed class BrokerMftBlockProducer
{
    readonly Func<CancellationToken, Task<JournalBrokerClient>> _connectAsync;
    readonly BrokerScanOptions? _scanOptions;
    readonly Action<BrokerScanResult>? _scanCompleted;

    /// <summary>Builds a producer over the given broker connection and scan defaults.</summary>
    /// <param name="connectAsync">
    ///     Yields the client each scan runs on. The caller keeps ownership of what it returns.
    /// </param>
    /// <param name="scanOptions">
    ///     Base options for every scan. The per-drive block target is filled in from the index
    ///     request, so a target supplied here is replaced.
    /// </param>
    /// <param name="scanCompleted">
    ///     Invoked with the scan result once it has passed validation, and not at all when the
    ///     drive errored or the block was rejected. The callback must not retain the result's
    ///     blocks: ownership of the block passes to the index as soon as this producer returns,
    ///     and the producer disposes it on every failure path.
    /// </param>
    public BrokerMftBlockProducer(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        BrokerScanOptions? scanOptions = null,
        Action<BrokerScanResult>? scanCompleted = null)
    {
        _connectAsync = connectAsync;
        _scanOptions = scanOptions;
        _scanCompleted = scanCompleted;
    }

    public MftBlockProducer CreateProducer() => ProduceAsync;

    /// <summary>
    ///     The live-watch half of this producer. The index starts one stream on it over every
    ///     drive it wants watched, and arms and disarms single drives on that stream through the
    ///     same object; the broker connection is the same borrowed one the producer used.
    /// </summary>
    public IIndexWatchSource CreateWatchSource()
    {
        return new BrokerIndexWatchSource(_connectAsync);
    }

    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var drive = JournalBrokerClient.NormalizeDriveLetter(request.DriveLetter.ToString());
        var brokerProgress = BrokerProgressAdapter.Create(request, _scanOptions?.Progress);
        var options = (_scanOptions ?? new BrokerScanOptions()) with
        {
            Progress = brokerProgress,
            BlockTargets = new Dictionary<string, BlockScanTarget>
            {
                [drive] = new(request.BlockPath, request.VolumeSerial, request.DeleteOnClose)
                {
                    CacheTag = request.CacheTag
                }
            }
        };
        var client = await _connectAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.ArmScanAndCatchUpAsync([drive], options, cancellationToken).ConfigureAwait(false);
        result.BlockOutcomes.TryGetValue(drive, out var outcome);
        try
        {
            if (result.Errors.TryGetValue(drive, out var error))
            {
                throw new InvalidOperationException(error);
            }

            if (outcome == null)
            {
                throw new InvalidOperationException($"No block outcome received for drive {drive}.");
            }

            if (!result.ArmedCursors.TryGetValue(drive, out var armed))
            {
                throw new InvalidOperationException($"No armed journal cursor received for drive {drive}.");
            }

            ValidateBlock(outcome.Block, request.VolumeSerial, armed, request.CacheTag);
            _scanCompleted?.Invoke(result);
            return new MftBlockProduceResult(outcome.Block, armed.JournalId, armed.NextUsn,
                SkippedRecordCount: checked((int)outcome.SkippedRecordCount), CompactionNeeded: outcome.Block.Header.IsCompactionNeeded);
        }
        catch
        {
            outcome?.Block.Dispose();
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

        // Checked here, before the caller invokes its validated-result callback: a block that
        // carries a different tag than requested is a producer failure, exactly like a wrong
        // journal cursor above, and must not be handed to the callback or returned as adopted.
        if (header.CacheTag != requestedCacheTag)
        {
            throw new InvalidOperationException(
                $"Block cache tag {header.CacheTag} does not match the requested tag {requestedCacheTag}.");
        }
    }
}
