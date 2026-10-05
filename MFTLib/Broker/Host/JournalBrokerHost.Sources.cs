using System.Runtime.CompilerServices;

namespace MFTLib;

/// <summary>The production volume seams <see cref="CreateDefault()" /> wires.</summary>
public sealed partial class JournalBrokerHost
{
    /// <summary>The MFT bytes a host scan reads per chunk, so a progress callback lands at least this often.</summary>
    internal const long HostScanChunkBytes = 64L * 1024 * 1024;

    /// <summary>Creates the host that scans, reads and watches real volumes through the native seams of MFTLib itself.</summary>
    /// <returns>A host whose volume access needs the elevation that raw volume handles require.</returns>
    public static JournalBrokerHost CreateDefault()
    {
        return CreateDefault(null);
    }

    /// <summary>The production host on a given clock, so a test can drive its timeouts.</summary>
    internal static JournalBrokerHost CreateDefault(TimeProvider? timeProvider)
    {
        return new JournalBrokerHost(
            QueryCursor,
            ScanDriveRecordBatches,
            ReadJournal,
            WatchAndDisposeAsync,
            QueryVolumeInfo,
            GrowUsnJournal,
            timeProvider: timeProvider);
    }

    /// <summary>The records per chunk that read <see cref="HostScanChunkBytes" /> of MFT.</summary>
    internal static uint HostScanChunkRecords(long bytesPerFileRecordSegment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerFileRecordSegment);
        return (uint)Math.Max(1, HostScanChunkBytes / bytesPerFileRecordSegment);
    }

    // Supplies native record batches without path resolution for the block writer. The scan's
    // allowance and token reach the native parse, so a rebalance takes effect at its next chunk
    // and a closed pipe stops it.
    static IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(string driveLetter,
        ParseThreadAllowance parseThreads, IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        operation.WaitingOnVolume();
        var bytesPerFileRecordSegment = QueryVolumeInfo(driveLetter).BytesPerFileRecordSegment;
        using var volume = MftVolume.Open(Bare(driveLetter), HostScanChunkRecords(bytesPerFileRecordSegment));
        operation.Processing("MFT parse");
        var mftProgress = CreateMftProgressAdapter(operation, progress);
        foreach (var batch in volume.ReadRecordBatches(resolvePaths: false, 4096, mftProgress, parseThreads,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = Array.FindAll(batch, record => record.InUse);
            if (records.Length > 0)
            {
                yield return records;
            }
        }
    }

    // Synchronous delivery preserves the parse total before the transfer phase reports its
    // smaller live-record count. Every native progress callback is also a processing step.
    static DirectProgress<MftScanProgress> CreateMftProgressAdapter(IBrokerOperationReporter operation,
        IProgress<BlockWriteProgress>? progress)
    {
        return new DirectProgress<MftScanProgress>(value =>
        {
            operation.Processing("MFT parse");
            progress?.Report(new BlockWriteProgress(value.RecordsScanned, 0, value.TotalRecords, null,
                BrokerScanPhase.Parsing));
        });
    }

    // Open the volume, stream cursor-tagged batches until cancelled, and dispose the volume when
    // the watch ends. The native read blocks until the journal changes, so the source reports
    // waiting on the volume before each read and processing once a batch arrives.
    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchAndDisposeAsync(
        string drive, UsnJournalCursor since, IBrokerOperationReporter operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        operation.WaitingOnVolume();
        using var volume = MftVolume.Open(Bare(drive));
        var batches = volume.WatchUsnJournal(since, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                operation.WaitingOnVolume();
                if (!await batches.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                operation.Processing("journal batch");
                yield return batches.Current;
            }
        }
        finally
        {
            await batches.DisposeAsync().ConfigureAwait(false);
        }
    }

    static UsnJournalSettings GrowUsnJournal(string drive, long maximumSize, long allocationDelta)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.GrowUsnJournal(maximumSize, allocationDelta);
    }

    static UsnJournalCursor QueryCursor(string drive)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.QueryUsnJournalCursor();
    }

    // NtfsVolumeInformation.Query is [SupportedOSPlatform("windows")]; the explicit
    // OperatingSystem.IsWindows() guard (rather than marking this method or its callers
    // windows-only) lets a broker built for this cross-platform library still throw a clear
    // PlatformNotSupportedException on a non-Windows host instead of failing to compile there.
    static NtfsVolumeInformation QueryVolumeInfo(string drive)
    {
        return OperatingSystem.IsWindows()
            ? NtfsVolumeInformation.Query(Bare(drive))
            : throw new PlatformNotSupportedException(
                "NTFS volume information queries require Windows (FSCTL_GET_NTFS_VOLUME_DATA).");
    }

    static (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(string drive, UsnJournalCursor since,
        int maximumBufferReads)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.ReadUsnJournalBounded(since, maximumBufferReads);
    }

    static string Bare(string drive)
    {
        return drive.TrimEnd(':', '\\', '/');
    }
}
