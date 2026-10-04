namespace MFTLib.Index;

/// <summary>
///     Where an index gets the blocks of its MFT-backed drives and the live watch of each. Obtained
///     from <c>BrokerMftBlockProducer.CreateIndexSource</c>, or from <see cref="Unavailable" />
///     when this process cannot scan.
/// </summary>
public sealed class MftIndexSource
{
    /// <summary>Carries a block producer and, optionally, the watch source that runs beside it.</summary>
    /// <param name="producer">Fills the block that each MFT-backed drive scan requests.</param>
    /// <param name="watchSource">
    ///     Starts each drive's live watch. Null means the index cannot watch an MFT-backed drive:
    ///     <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> throws
    ///     <see cref="InvalidOperationException" />.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="producer" /> is null.</exception>
    public MftIndexSource(MftBlockProducer producer, IIndexWatchSource? watchSource = null)
    {
        ArgumentNullException.ThrowIfNull(producer);
        Producer = producer;
        WatchSource = watchSource;
    }

    MftIndexSource(string unavailableReason)
    {
        UnavailableReason = unavailableReason;
        Producer = (request, _) => Task.FromException<MftBlockProduceResult>(
            new InvalidOperationException(FormatUnavailable(request.DriveLetter, unavailableReason)));
    }

    internal MftBlockProducer Producer { get; }

    internal IIndexWatchSource? WatchSource { get; }

    internal string? UnavailableReason { get; }

    /// <summary>
    ///     A source that scans nothing and watches nothing. Every scan of a drive fails with
    ///     <see cref="InvalidOperationException" /> whose message is <c>"Drive {letter}: {reason}."</c>,
    ///     which the index reports as <see cref="DriveFailureKind.ProducerFailed" /> with that message
    ///     in <see cref="DriveStatus.MftProducerFailureMessage" />. Starting a watch throws
    ///     <see cref="InvalidOperationException" /> with the same message. Cached blocks still open.
    /// </summary>
    /// <param name="reason">Why this process cannot scan, written so that it ends a sentence.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is null or white space.</exception>
    public static MftIndexSource Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new MftIndexSource(reason);
    }

    internal static string FormatUnavailable(char driveLetter, string reason) => $"Drive {driveLetter}: {reason}.";
}
