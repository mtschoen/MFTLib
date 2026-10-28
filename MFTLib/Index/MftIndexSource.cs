namespace MFTLib.Index;

/// <summary>
///     Where an index gets the blocks of its MFT-backed volumes and the live watch of each.
///     Obtained from <see cref="MFTLib.BrokerSession.CreateIndexSource" /> or
///     <see cref="MFTLib.MftIndexSources" />. Leave <see cref="FileIndexOptions.MftSource" />
///     null when no source is available.
/// </summary>
public sealed class MftIndexSource
{
    /// <summary>Carries a block producer and, optionally, the watch source that runs beside it.</summary>
    /// <param name="producer">Fills the block that each MFT-backed volume scan requests.</param>
    /// <param name="watchSource">
    ///     Starts each volume's live watch. Null means the index cannot watch an MFT-backed volume:
    ///     <see cref="FileIndex.StartWatchingAsync" /> returns a per-root
    ///     <see cref="RootOperationOutcome.NotApplicable" /> result with no failure and records no
    ///     watch request.
    /// </param>
    /// <param name="dumpIdentity">
    ///     Set when the producer loads an MFT dump file. The index then treats the volume as a virtual
    ///     inventory: it is never probed, opened, journaled or watched as the live volume that shares
    ///     its letter, and the options it opens with are validated by
    ///     <see cref="MftDumpSourceIdentity.ValidateOptions" />.
    /// </param>
    /// <param name="includeFreed">
    ///     Whether the producer writes freed records, taken from its scan options. Part of the scan shape an MFT
    ///     block must carry.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="producer" /> is null.</exception>
    /// <exception cref="ArgumentException">A dump source was given a watch source.</exception>
    internal MftIndexSource(MftBlockProducer producer, IIndexWatchSource? watchSource = null,
        MftDumpSourceIdentity? dumpIdentity = null, bool includeFreed = false)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (dumpIdentity is not null && watchSource is not null)
        {
            throw new ArgumentException("A dump source cannot watch.", nameof(watchSource));
        }

        Producer = producer;
        WatchSource = watchSource;
        DumpIdentity = dumpIdentity;
        IncludeFreed = includeFreed;
    }


    internal MftBlockProducer Producer { get; }

    internal IIndexWatchSource? WatchSource { get; }


    internal MftDumpSourceIdentity? DumpIdentity { get; }

    /// <summary>Whether the producer writes freed records. Part of an MFT block's scan shape.</summary>
    internal bool IncludeFreed { get; }

    internal const string NoWatchReason = "this source does not support watching";

    internal const string NoJournalSettingsReason = "this source has no live journal settings";


    internal static string FormatUnavailable(VolumeKey volume, string reason) => $"Volume {volume}: {reason}.";
}
