using MFTLib.Index;

namespace MFTLib;

/// <summary>
/// Internal volume operations required by block production and the broker host.
/// Methods may throw, streams stay lazy, disposal terminates their volume lifetime,
/// the scan preserves the mutable allowance/options/cancellation token, and bounded
/// journal reads preserve their maximum-buffer-read contract.
/// </summary>
interface IVolumeAccess
{
    UsnJournalCursor QueryCursor(VolumeKey volume);

    /// <summary>
    ///     Opens the volume for a scan whose slices <see cref="MftBlockRowWriter" /> writes while it parses. The scan
    ///     starts when its slices are enumerated; disposing the source waits for it to end and closes the volume.
    ///     <paramref name="expectedVolumeSerial" /> is the full serial the block being written records; it is required
    ///     so that every scan checks it on the handle it parses, and a mismatch throws <see cref="IOException" />
    ///     before any row.
    /// </summary>
    IMftSliceSource OpenVolumeScan(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, ulong expectedVolumeSerial, CancellationToken cancellationToken);

    (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey volume, UsnJournalCursor since, int maximumBufferReads);
    IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken);
    NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume);

    /// <summary>
    ///     Reads the resident <c>$DATA</c> of the requested records of the volume's MFT, which needs the same
    ///     access as a scan. Answers every request exactly once through <paramref name="sink" />, serially and in
    ///     unspecified order, on the calling thread; an exception the sink throws propagates. The volume is opened
    ///     for the call and closed before it returns.
    /// </summary>
    /// <param name="volume">The volume to read.</param>
    /// <param name="volumeSerial">
    ///     The full serial the scanned block records. The read is refused with <see cref="IOException" /> before any
    ///     answer when it is zero or differs from the serial of the opened volume.
    /// </param>
    /// <param name="requests">The requests, ascending by record number.</param>
    /// <param name="sink">Receives each answer.</param>
    /// <param name="cancellationToken">Stops the read with <see cref="OperationCanceledException" />.</param>
    void ReadInlineContent(VolumeKey volume, ulong volumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads the name and parent a scan gives each requested file from the volume's MFT, which needs the same
    ///     access as a scan. Answers every request exactly once through <paramref name="sink" />, as
    ///     <see cref="ReadInlineContent" /> does; a returned answer is the 8-byte parent file reference followed by
    ///     the name's UTF-16 units. The volume is opened for the call and closed before it returns. A watch asks this
    ///     of the files a journal batch reports a hard-link change for, on the volume whose journal it reads.
    /// </summary>
    /// <param name="volume">The volume to read.</param>
    /// <param name="requests">The requests, ascending by record number.</param>
    /// <param name="sink">Receives each answer.</param>
    /// <param name="cancellationToken">Stops the read with <see cref="OperationCanceledException" />.</param>
    void ReadFileNames(VolumeKey volume, ReadOnlyMemory<InlineContentRequest> requests, InlineContentSink sink,
        CancellationToken cancellationToken);

    UsnJournalGrowthResult GrowUsnJournal(
        VolumeKey volume,
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize = false);
}
