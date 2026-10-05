using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     The MFT block producer of a test index: writes the rows a test names through the real
///     block writer and returns a real <see cref="MftBlockProduceResult" />, copying the
///     request's cache tag as every custom producer must.
/// </summary>
public sealed class SyntheticMftProducer
{
    readonly Func<char, IEnumerable<SyntheticRow>> _rowsForDrive;
    readonly object _gate = new();
    readonly List<char> _producedDrives = [];

    /// <summary>Creates a producer.</summary>
    /// <param name="rowsForDrive">The rows of a drive's block, including row 5, the root. Called once per production.</param>
    public SyntheticMftProducer(Func<char, IEnumerable<SyntheticRow>> rowsForDrive)
    {
        ArgumentNullException.ThrowIfNull(rowsForDrive);
        _rowsForDrive = rowsForDrive;
    }

    /// <summary>A watch started after the scan resumes from this position, so set it to what the test's watch source expects.</summary>
    public UsnJournalCursor JournalCursor { get; set; }

    /// <summary>Becomes <see cref="DriveStatus.ScanTimestamp" />, so a test about scan age sets it explicitly.</summary>
    public DateTime CompletedUtc { get; set; }

    /// <summary>Becomes <see cref="DriveStatus.SkippedRecordCount" />; zero means the scan placed every record.</summary>
    public int SkippedRecordCount { get; set; }

    /// <summary>Awaited before each production builds its block, so a test can hold a scan or rescan in progress.</summary>
    public Func<char, CancellationToken, Task>? BeforeProduceAsync { get; set; }

    /// <summary>The proven catch-up loss a production reports; null, or a null result, reports none.</summary>
    public Func<char, JournalCheckpointLoss?>? CatchUpLoss { get; set; }

    /// <summary>Every drive whose production has started, in order, including repeats and productions held by <see cref="BeforeProduceAsync" />.</summary>
    public IReadOnlyList<char> ProducedDrives
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
            _producedDrives.Add(request.DriveLetter);
        }

        var hook = BeforeProduceAsync;
        if (hook is not null)
        {
            await hook(request.DriveLetter, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cursor = JournalCursor;
        return Describe(SyntheticBlock.Build(request.BlockPath, request.VolumeSerial, request.DeleteOnClose,
            new SyntheticBlockOptions
            {
                JournalCursor = cursor,
                CompletedUtc = CompletedUtc,
                CacheTag = request.CacheTag
            }, _rowsForDrive(request.DriveLetter)), cursor, request.DriveLetter);
    }

    /// <summary>Wraps the built block, closing it when the catch-up loss callback throws so no handle leaks.</summary>
    MftBlockProduceResult Describe(BlockFile block, UsnJournalCursor cursor, char driveLetter)
    {
        try
        {
            return new MftBlockProduceResult(block, cursor.JournalId, cursor.NextUsn, SkippedRecordCount)
            {
                CatchUpLoss = CatchUpLoss?.Invoke(driveLetter)
            };
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }
}
