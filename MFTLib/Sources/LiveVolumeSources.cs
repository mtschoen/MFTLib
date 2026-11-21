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

    // Opens the volume and prepares its streaming parse for the block writer, without resolving paths. The scan's
    // allowance and token reach the native parse, so a rebalance takes effect at its next chunk and a closed pipe
    // stops it. The returned source owns the volume. The full serial of the scan handle must equal the one the block
    // records, so the block's header serial proves which volume its rows came from.
    internal static IMftSliceSource OpenVolumeScan(VolumeKey volume, ParseThreadAllowance parseThreads,
        BlockWriteReporting reporting, MftScanOptions scanOptions, ulong expectedVolumeSerial,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        reporting.Operation?.WaitingOnVolume();
        var bytesPerFileRecordSegment = QueryVolumeInfo(volume).BytesPerFileRecordSegment;
        // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to the returned stream, which closes it once the parse has returned
        var mftVolume = MftVolume.Open(volume, HostScanChunkRecords(bytesPerFileRecordSegment));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RequireVolumeSerial(mftVolume, volume, expectedVolumeSerial);
            }
            reporting.Operation?.Processing("MFT parse");
            var mftProgress = MftBlockProgressAdapter.CreateParse(reporting.Progress, reporting.Operation);
            return mftVolume.OpenSliceStream(mftProgress, parseThreads, scanOptions, cancellationToken);
        }
        catch
        {
            mftVolume.Dispose();
            throw;
        }
    }

    // Opens the volume, reads the resident content of the requested records through the native reader and closes
    // the volume. The reader is disposed before the volume it borrows. A block that records no serial cannot prove
    // which volume it was scanned from, so its read is refused before the volume is opened.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void ReadInlineContent(VolumeKey volume, ulong volumeSerial,
        ReadOnlyMemory<InlineContentRequest> requests, InlineContentSink sink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (volumeSerial == 0)
        {
            throw new IOException(
                $"The block of volume {volume} records no volume serial, so the volume cannot be confirmed as the one scanned.");
        }

        using var mftVolume = MftVolume.Open(volume);
        RequireVolumeSerial(mftVolume, volume, volumeSerial);
        using var reader = mftVolume.OpenInlineContentReader();
        reader.Read(requests.Span, sink, cancellationToken);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void RequireVolumeSerial(MftVolume opened, VolumeKey volume, ulong expected)
    {
        if (opened.QueryVolumeInformation().VolumeSerialNumber != expected)
        {
            throw new IOException($"Volume {volume} does not have the expected serial.");
        }
    }

    // Open the volume, stream cursor-tagged batches until cancelled, and dispose the volume when
    // the watch ends. The native read blocks until the journal changes, so the source reports
    // waiting on the volume before each read and processing once a batch arrives.
    internal static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchAndDisposeAsync(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        operation.WaitingOnVolume();
        using var mftVolume = MftVolume.Open(volume);
        var batches = mftVolume.Journal.WatchUsnJournal(since, cancellationToken)
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

    internal static UsnJournalGrowthResult GrowUsnJournal(
        VolumeKey volume,
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize = false)
    {
        using var mftVolume = MftVolume.Open(volume);
        return mftVolume.Journal.GrowUsnJournal(maximumSize, allocationDelta, allowAboveMaximumRecommendedSize);
    }

    internal static UsnJournalCursor QueryCursor(VolumeKey volume)
    {
        using var mftVolume = MftVolume.Open(volume);
        return mftVolume.Journal.QueryUsnJournalCursor();
    }

    // The internal overload carries the platform refusal, so a broker built for this cross-platform
    // library throws a clear PlatformNotSupportedException on a non-Windows host instead of failing to
    // compile there.
    internal static NtfsVolumeInformation QueryVolumeInfo(VolumeKey volume)
    {
        return NtfsVolumeInformation.Query(volume, OperatingSystem.IsWindows());
    }

    internal static (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(VolumeKey volume, UsnJournalCursor since,
        int maximumBufferReads)
    {
        using var mftVolume = MftVolume.Open(volume);
        return mftVolume.Journal.ReadUsnJournalBounded(since, maximumBufferReads);
    }
}
