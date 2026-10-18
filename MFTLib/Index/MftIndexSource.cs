namespace MFTLib.Index;

/// <summary>
///     Where an index gets the blocks of its MFT-backed drives and the live watch of each. Obtained
///     from <see cref="MFTLib.BrokerSession.CreateIndexSource" />, <see cref="MFTLib.MftIndexSources" />, or from
///     <see cref="MFTLib.MftIndexSources.Unavailable" /> when this process cannot scan.
/// </summary>
public sealed class MftIndexSource
{
    /// <summary>Carries a block producer and, optionally, the watch source that runs beside it.</summary>
    /// <param name="producer">Fills the block that each MFT-backed drive scan requests.</param>
    /// <param name="watchSource">
    ///     Starts each drive's live watch. Null means the index cannot watch an MFT-backed drive:
    ///     <see cref="FileIndex.StartWatchingAsync" /> returns a per-drive
    ///     <see cref="DriveOperationOutcome.NotApplicable" /> result with no failure and records no
    ///     watch request.
    /// </param>
    /// <param name="dumpIdentity">
    ///     Set when the producer loads an MFT dump file. The index then treats the drive as a virtual
    ///     inventory: it is never probed, opened, journaled or watched as the live volume that shares
    ///     its letter, and the options it opens with are validated by
    ///     <see cref="MftDumpSourceIdentity.ValidateOptions" />.
    /// </param>
    /// <param name="validateRetainedFileNames">
    ///     Throws <see cref="ArgumentException" /> when the producer cannot carry the retained file names to
    ///     its scanner; null accepts every set. <see cref="FileIndex.OpenAsync" /> calls it before any side effect.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="producer" /> is null.</exception>
    /// <exception cref="ArgumentException">A dump source was given a watch source.</exception>
    internal MftIndexSource(MftBlockProducer producer, IIndexWatchSource? watchSource = null,
        MftDumpSourceIdentity? dumpIdentity = null, Action<IReadOnlyCollection<string>>? validateRetainedFileNames = null)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (dumpIdentity is not null && watchSource is not null)
        {
            throw new ArgumentException("A dump source cannot watch.", nameof(watchSource));
        }

        Producer = producer;
        WatchSource = watchSource;
        DumpIdentity = dumpIdentity;
        ValidateRetainedFileNames = validateRetainedFileNames;
    }

    MftIndexSource(string unavailableReason)
    {
        UnavailableReason = unavailableReason;
        Producer = (request, _) => Task.FromException<MftBlockProduceResult>(
            new InvalidOperationException(FormatUnavailable(request.Drive, unavailableReason)));
    }

    internal MftBlockProducer Producer { get; }

    internal IIndexWatchSource? WatchSource { get; }

    internal string? UnavailableReason { get; }

    internal MftDumpSourceIdentity? DumpIdentity { get; }

    internal Action<IReadOnlyCollection<string>>? ValidateRetainedFileNames { get; }

    internal const string NoWatchReason = "this source does not support watching";

    internal const string NoJournalSettingsReason = "this source has no live journal settings";

    internal static MftIndexSource CreateUnavailable(string reason) => new(reason);

    internal static string FormatUnavailable(DriveKey drive, string reason) => $"Drive {drive}: {reason}.";
}
