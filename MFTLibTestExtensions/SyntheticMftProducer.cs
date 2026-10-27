using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     The MFT block producer of a test index: writes the rows a test names through the real
///     block writer and returns a real produce result, copying the
///     request's cache tag and scan shape as every custom producer must.
/// </summary>
public sealed class SyntheticMftProducer
{
    readonly Func<VolumeKey, IEnumerable<SyntheticRow>> _rowsForDrive;
    readonly Lock _gate = new();
    readonly List<VolumeKey> _producedDrives = [];

    /// <summary>Creates a producer.</summary>
    /// <param name="rowsForVolume">The rows of a volume's block, including row 5, the root. Called once per production.</param>
    public SyntheticMftProducer(Func<VolumeKey, IEnumerable<SyntheticRow>> rowsForVolume)
    {
        ArgumentNullException.ThrowIfNull(rowsForVolume);
        _rowsForDrive = rowsForVolume;
    }

    /// <summary>A watch started after the scan resumes from this position, so set it to what the test's watch source expects.</summary>
    public SyntheticJournalCursor JournalCursor { get; init; }

    /// <summary>Becomes <see cref="DriveBlockStatus.ScanTimestamp" />, so a test about scan age sets it explicitly.</summary>
    public DateTime ScanTimestamp { get; init; }

    // SyntheticBlockTests.Producer_WritesItsRowsThroughTheRealWriterAndReportsItsSettings
    // uses this to verify propagation of a producer's skipped-record count.
    /// <summary>Becomes <see cref="DriveBlockStatus.SkippedRecordCount" />; zero means the scan placed every record.</summary>
    internal int SkippedRecordCount { get; init; }

    /// <summary>Awaited before each production builds its block, so a test can hold a scan or rescan in progress.</summary>
    public Func<VolumeKey, CancellationToken, Task>? BeforeProduceAsync { get; init; }

    /// <summary>The proven catch-up loss a production reports; null, or a null result, reports none.</summary>
    public Func<VolumeKey, JournalCheckpointLoss?>? CatchUpLoss { get; init; }

    /// <summary>Every drive whose production has started, in order, including repeats and productions held by <see cref="BeforeProduceAsync" />.</summary>
    internal IReadOnlyList<VolumeKey> ProducedDrives
    {
        get
        {
            lock (_gate)
            {
                return [.. _producedDrives];
            }
        }
    }

    /// <summary>The producer as the delegate <see cref="SyntheticIndexSource" /> hands to an index.</summary>
    internal MftBlockProducer Producer => ProduceAsync;

    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _producedDrives.Add(request.Drive);
        }

        var hook = BeforeProduceAsync;
        if (hook is not null)
        {
            await hook(request.Drive, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cursor = JournalCursor;
        return Describe(SyntheticBlock.Build(request.BlockPath, request.Drive, request.VolumeSerial, request.DeleteOnClose,
            new SyntheticBlockOptions
            {
                JournalCursor = cursor,
                ScanTimestamp = ScanTimestamp,
                CacheTag = request.CacheTag,
                ScanShape = request.ScanShape
            }, _rowsForDrive(request.Drive)), request.Drive);
    }

    /// <summary>Wraps the built block, closing it when the catch-up loss callback throws so no handle leaks.</summary>
    MftBlockProduceResult Describe(BlockFile block, VolumeKey drive)
    {
        try
        {
            return new MftBlockProduceResult(block, SkippedRecordCount)
            {
                CatchUpLoss = CatchUpLoss?.Invoke(drive)
            };
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }
}
