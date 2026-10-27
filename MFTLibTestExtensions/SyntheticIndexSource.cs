using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Builds the MFT source of a test index from the synthetic producer and the scripted watch source.</summary>
public static class SyntheticIndexSource
{
    /// <summary>Creates a source whose scans come from <paramref name="producer" /> and whose watches from <paramref name="watches" />.</summary>
    /// <param name="producer">
    ///     Produces each root's block. Null builds a source whose scans fail with "no synthetic producer was supplied".
    /// </param>
    /// <param name="watches">Starts each volume's watch. Null means the index cannot watch an MFT-backed volume.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    public static MftIndexSource Create(SyntheticMftProducer? producer = null, ScriptedWatchSource? watches = null)
    {
        return new MftIndexSource(producer?.Producer ?? RefuseScan, watches?.IndexSource);
    }

    static Task<MftBlockProduceResult> RefuseScan(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        return Task.FromException<MftBlockProduceResult>(
            new InvalidOperationException($"Root {request.Volume}: no synthetic producer was supplied."));
    }
}
