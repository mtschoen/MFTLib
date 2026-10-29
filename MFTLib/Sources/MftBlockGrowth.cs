using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Re-runs an MFT scan into a larger block when the planned name entries or name pool ran out, so a cold
///     production completes instead of publishing an incomplete block. The planned entry count is a fraction of the
///     slot count; a volume with many distinct names exceeds it. Row exhaustion is not retried.
/// </summary>
static class MftBlockGrowth
{
    /// <summary>Runs the scan, and again with the exhausted regions enlarged, until a block fits or cannot grow.</summary>
    internal static MftBlockProduceResult ProduceUntilFit(MftBlockProduceRequest request,
        Func<MftBlockProduceRequest, MftBlockProduceResult> produce)
    {
        while (true)
        {
            var result = produce(request);
            if (!TryGrow(request, result, out var next))
            {
                return result;
            }

            request = next;
        }
    }

    /// <summary>Async form of <see cref="ProduceUntilFit" /> for a producer that runs in another process.</summary>
    internal static async Task<MftBlockProduceResult> ProduceUntilFitAsync(MftBlockProduceRequest request,
        Func<MftBlockProduceRequest, Task<MftBlockProduceResult>> produce)
    {
        while (true)
        {
            var result = await produce(request).ConfigureAwait(false);
            if (!TryGrow(request, result, out var next))
            {
                return result;
            }

            request = next;
        }
    }

    // On growth the exhausted block is disposed and its file deleted (a delete-on-close block deletes itself),
    // so the retry creates the file anew while the caller still holds its cache owner lock.
    static bool TryGrow(MftBlockProduceRequest request, MftBlockProduceResult result, out MftBlockProduceRequest next)
    {
        var header = result.Block.Header;
        if (!BlockLayout.RetryGrowsCapacity(in header))
        {
            next = request;
            return false;
        }

        var retry = BlockLayout.RetryMinimum(in header);
        result.Block.Dispose();
        if (!request.DeleteOnClose)
        {
            BlockFile.TryDeleteFailedCreate(request.BlockPath, request.Diagnostics,
                $"the scan exhausted {header.NameEntryCapacity} name entries and {header.NamePoolCapacity} name-pool bytes; " +
                "it runs again with the exhausted regions grown");
        }

        next = request with { MinimumCapacities = retry.AtLeast(request.MinimumCapacities) };
        return true;
    }
}
