using System.Runtime.CompilerServices;

namespace MFTLib;

/// <summary>The production volume seams: the native scan, journal and volume calls a broker host and a direct producer share.</summary>
internal static class LiveVolumeSources
{
    /// <summary>The MFT bytes a host scan reads per chunk, so a progress callback lands at least this often.</summary>
    internal const long HostScanChunkBytes = 64L * 1024 * 1024;

    /// <summary>The records per chunk that read <see cref="HostScanChunkBytes" /> of MFT.</summary>
    internal static uint HostScanChunkRecords(long bytesPerFileRecordSegment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerFileRecordSegment);
        return (uint)Math.Max(1, HostScanChunkBytes / bytesPerFileRecordSegment);
    }

    // Supplies native record batches without path resolution for the block writer. The scan's
    // allowance and token reach the native parse, so a rebalance takes effect at its next chunk
    // and a closed pipe stops it.
    internal static IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(string driveLetter,
        ParseThreadAllowance parseThreads, IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        operation.WaitingOnVolume();
        var bytesPerFileRecordSegment = QueryVolumeInfo(driveLetter).BytesPerFileRecordSegment;
        using var volume = MftVolume.Open(Bare(driveLetter), HostScanChunkRecords(bytesPerFileRecordSegment));
        operation.Processing("MFT parse");
        var mftProgress = CreateMftProgressAdapter(operation, progress);
        ulong unreadableRecords = 0;
        foreach (var batch in volume.ReadRecordBatches(resolvePaths: false, 4096, mftProgress, parseThreads,
                     cancellationToken, count => unreadableRecords = count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = Array.FindAll(batch, record => record.InUse);
            if (records.Length > 0)
            {
                yield return records;
            }
        }

        // A record the parser could not trust has no row, so it is counted with the skipped ones.
        if (unreadableRecords > 0)
        {
            yield return new MftOmittedRecords(checked((long)unreadableRecords));
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
    internal static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchAndDisposeAsync(
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

    internal static UsnJournalSettings GrowUsnJournal(string drive, long maximumSize, long allocationDelta)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.GrowUsnJournal(maximumSize, allocationDelta);
    }

    internal static UsnJournalCursor QueryCursor(string drive)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.QueryUsnJournalCursor();
    }

    // NtfsVolumeInformation.Query is [SupportedOSPlatform("windows")]; the explicit
    // OperatingSystem.IsWindows() guard (rather than marking this method or its callers
    // windows-only) lets a broker built for this cross-platform library still throw a clear
    // PlatformNotSupportedException on a non-Windows host instead of failing to compile there.
    internal static NtfsVolumeInformation QueryVolumeInfo(string drive)
    {
        return OperatingSystem.IsWindows()
            ? NtfsVolumeInformation.Query(Bare(drive))
            : throw new PlatformNotSupportedException(
                "NTFS volume information queries require Windows (FSCTL_GET_NTFS_VOLUME_DATA).");
    }

    internal static (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(string drive, UsnJournalCursor since,
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
