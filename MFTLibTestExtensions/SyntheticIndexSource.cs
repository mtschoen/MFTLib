using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Builds the MFT source of a test index from the synthetic producer and the scripted watch source.</summary>
public static class SyntheticIndexSource
{
    /// <summary>Creates a source whose scans come from <paramref name="producer" /> and whose watches from <paramref name="watches" />.</summary>
    /// <param name="producer">
    ///     Produces each drive's block. Null builds a source whose scans fail with "no synthetic producer was supplied".
    /// </param>
    /// <param name="watches">Starts each drive's watch. Null means the index cannot watch an MFT-backed drive.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    public static MftIndexSource Create(SyntheticMftProducer? producer = null, ScriptedWatchSource? watches = null)
    {
        return new MftIndexSource(producer?.Producer ?? RefuseScan, watches);
    }

    static Task<MftBlockProduceResult> RefuseScan(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        return Task.FromException<MftBlockProduceResult>(
            new InvalidOperationException($"Drive {request.DriveLetter}: no synthetic producer was supplied."));
    }
}
