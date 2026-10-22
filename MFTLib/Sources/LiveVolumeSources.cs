using System.Runtime.CompilerServices;
using MFTLib.Index;

namespace MFTLib;

/// <summary>The production volume operations: the native scan, journal and volume calls a broker host and a direct producer share through <see cref="LiveVolumeAccess" />.</summary>
static class LiveVolumeSources
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
    internal static IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(DriveKey drive,
        ParseThreadAllowance parseThreads, BlockWriteReporting reporting, MftScanOptions scanOptions,
        Action<long> recordsOmitted, CancellationToken cancellationToken)
    {
        reporting.Operation?.WaitingOnVolume();
        var bytesPerFileRecordSegment = QueryVolumeInfo(drive).BytesPerFileRecordSegment;
        using var volume = MftVolume.Open(drive, HostScanChunkRecords(bytesPerFileRecordSegment));
        reporting.Operation?.Processing("MFT parse");
        var mftProgress = MftBlockProgressAdapter.CreateParse(reporting.Progress, reporting.Operation);
        foreach (var batch in volume.ReadRecordBatches(4096, mftProgress, parseThreads, cancellationToken,
                     scanOptions, recordsOmitted))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The parser applies IncludeFreed before materializing these batches.
            if (batch.Length > 0)
            {
                yield return batch;
            }
        }
    }

    // Open the volume, stream cursor-tagged batches until cancelled, and dispose the volume when
    // the watch ends. The native read blocks until the journal changes, so the source reports
    // waiting on the volume before each read and processing once a batch arrives.
    internal static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchAndDisposeAsync(
        DriveKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        operation.WaitingOnVolume();
        using var volume = MftVolume.Open(drive);
        var batches = volume.Journal.WatchUsnJournal(since, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
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

    internal static UsnJournalSettings GrowUsnJournal(DriveKey drive, long maximumSize, long allocationDelta)
    {
        using var volume = MftVolume.Open(drive);
        return volume.Journal.GrowUsnJournal(maximumSize, allocationDelta);
    }

    internal static UsnJournalCursor QueryCursor(DriveKey drive)
    {
        using var volume = MftVolume.Open(drive);
        return volume.Journal.QueryUsnJournalCursor();
    }

    // NtfsVolumeInformation.Query is [SupportedOSPlatform("windows")]; the explicit
    // OperatingSystem.IsWindows() guard (rather than marking this method or its callers
    // windows-only) lets a broker built for this cross-platform library still throw a clear
    // PlatformNotSupportedException on a non-Windows host instead of failing to compile there.
    internal static NtfsVolumeInformation QueryVolumeInfo(DriveKey drive)
    {
        return OperatingSystem.IsWindows()
            ? NtfsVolumeInformation.Query(drive)
            : throw new PlatformNotSupportedException(
                "NTFS volume information queries require Windows (FSCTL_GET_NTFS_VOLUME_DATA).");
    }

    internal static (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(DriveKey drive, UsnJournalCursor since,
        int maximumBufferReads)
    {
        using var volume = MftVolume.Open(drive);
        return volume.Journal.ReadUsnJournalBounded(since, maximumBufferReads);
    }
}
