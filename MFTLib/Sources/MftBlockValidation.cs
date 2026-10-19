using MFTLib.Index;

namespace MFTLib;

internal static class MftBlockValidation
{
    internal static void Validate(BlockFile block, uint volumeSerial, UsnJournalCursor armed, CacheTag requestedCacheTag,
        ulong requestedScanShape)
    {
        var header = block.Header;
        if (header.RowCount == 0)
        {
            throw new InvalidOperationException("Block RowCount must be greater than zero.");
        }

        var validation = BlockHeader.Validate(in header, volumeSerial, block.Length);
        if (validation is not null)
        {
            throw new InvalidOperationException($"Block validation failed: {validation}.");
        }

        if (header.ProducerKind != ProducerKind.Mft)
        {
            throw new InvalidOperationException("Block ProducerKind must be Mft.");
        }

        if (header.UsnJournalIdentifier != armed.JournalIdentifier || header.UsnNextUsn != armed.NextUsn)
        {
            throw new InvalidOperationException("Block journal cursor does not match the armed cursor.");
        }

        // A block that carries a different tag than requested is a producer failure, exactly
        // like a wrong journal cursor above, and must not be returned as adopted.
        if (header.CacheTag != requestedCacheTag)
        {
            throw new InvalidOperationException(
                $"Block cache tag {header.CacheTag} does not match the requested tag {requestedCacheTag}.");
        }

        if (header.ScanShape != requestedScanShape)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Block scan shape {header.ScanShape:X16} does not match the requested shape {requestedScanShape:X16}."));
        }
    }

    internal static MftBlockProduceResult Finish(MftBlockProduceRequest request, BlockFile block,
        UsnJournalCursor armed, int skippedRecordCount, JournalCheckpointLoss? catchUpLoss)
    {
        try
        {
            Validate(block, request.VolumeSerial, armed, request.CacheTag, request.ScanShape);
            return new MftBlockProduceResult(block, skippedRecordCount) { CatchUpLoss = catchUpLoss };
        }
        catch (Exception exception)
        {
            block.Dispose();
            BlockFile.TryDeleteFailedCreate(request.BlockPath, request.Diagnostics,
                $"the MFT producer's completed block failed validation: {exception.Message}");
            throw;
        }
    }
}
