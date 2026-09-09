using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Produces index blocks through a broker. Clients returned by connectAsync remain owned by
///     the caller, which decides whether to share a client across drives. Index request
///     progress cannot represent broker phases; configure BrokerScanOptions.Progress instead.
/// </summary>
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
public sealed class BrokerMftBlockProducer(
    Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
    BrokerScanOptions? scanOptions = null,
    Action<BrokerScanResult>? scanCompleted = null)
{
    public MftBlockProducer CreateProducer() => ProduceAsync;

    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var drive = JournalBrokerClient.NormalizeDriveLetter(request.DriveLetter.ToString());
        var options = (scanOptions ?? new BrokerScanOptions()) with
        {
            BlockTargets = new Dictionary<string, BlockScanTarget>
            {
                [drive] = new(request.BlockPath, request.VolumeSerial, request.DeleteOnClose)
            }
        };
        var client = await connectAsync(cancellationToken).ConfigureAwait(false);
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

            ValidateBlock(outcome.Block, request.VolumeSerial, armed);
            scanCompleted?.Invoke(result);
            return new MftBlockProduceResult(outcome.Block, armed.JournalId, armed.NextUsn,
                SkippedRecordCount: checked((int)outcome.SkippedRecordCount), CompactionNeeded: outcome.Block.Header.IsCompactionNeeded);
        }
        catch
        {
            outcome?.Block.Dispose();
            throw;
        }
    }

    static void ValidateBlock(BlockFile block, uint volumeSerial, UsnJournalCursor armed)
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
    }
}
