using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Reads the records of one opened dump in materialized batches. The producer's own source
///     parses the input; a test substitutes batches no file can hold.
/// </summary>
internal delegate IEnumerable<IReadOnlyList<MftRecord>> MftDumpRecordSource(MftDumpInput input,
    IProgress<MftScanProgress>? progress, CancellationToken cancellationToken);

/// <summary>
///     Builds an index block from an MFT dump file, in process and without a broker. Each request
///     opens the file once and takes the block's sizing and every record from that one open file,
///     so a path replaced during a scan cannot mix two files, and the next request opens whatever
///     the path then names. It never touches the live volume that shares the dump's drive letter:
///     the block carries a zero journal cursor and the request's volume serial.
///     Dump and local-volume scans share the process's parse-thread allocator; cancellation,
///     failure and completion release each request's registration.
/// </summary>
internal sealed class MftDumpBlockProducer
{
    const int RecordBatchSize = 4096;

    readonly string _dumpFilePath;
    readonly MftDumpRecordSource? _readRecords;

    /// <summary>Builds a producer for one dump file.</summary>
    /// <param name="dumpFilePath">The full path of the dump, opened anew by each request.</param>
    /// <param name="readRecords">Reads the opened input's records; null parses the file.</param>
    internal MftDumpBlockProducer(string dumpFilePath, MftDumpRecordSource? readRecords = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFilePath);
        _dumpFilePath = dumpFilePath;
        _readRecords = readRecords;
    }

    /// <summary>
    ///     Produces the block the request names. The parse and the block write run on a thread-pool thread.
    /// </summary>
    /// <exception cref="IOException">The dump could not be opened.</exception>
    /// <exception cref="InvalidDataException">The dump's content was rejected; the message says which check failed.</exception>
    /// <exception cref="OperationCanceledException">The token stopped the scan.</exception>
    internal async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<MftBlockProduceResult>(cancellationToken).ConfigureAwait(false);
        }

        using var registration = await LocalMftBlockProducer.SharedAllocator.AdmitAsync(cancellationToken)
            .ConfigureAwait(false);
        var allowance = registration.Allowance;
        return await Task.Run(() => Produce(request, allowance, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    MftBlockProduceResult Produce(MftBlockProduceRequest request, ParseThreadAllowance allowance,
        CancellationToken cancellationToken)
    {
        using var input = MftDumpInput.Open(_dumpFilePath);

        // The first batch is read before the block exists: reading it runs the whole parse, so a
        // dump the parser rejects costs no block, whose size follows the dump's length.
        var progress = ParseProgress(request);
        var source = _readRecords is { } readRecords
            ? readRecords(input, progress, cancellationToken)
            : input.ReadRecordBatches(RecordBatchSize, progress, cancellationToken, allowance);
        using var batches = source.GetEnumerator();
        var hasBatch = batches.MoveNext();

        BlockFile? block = BlockFile.Create(MftBlockCapacity.CreateOptions(input.VolumeInformation,
            request.BlockPath, request.VolumeSerial, request.DeleteOnClose, request.CacheTag));
        try
        {
            var records = new MftDumpRecordValidation(block).Validate(Remaining(batches, hasBatch));

            // A dump has no journal: the zero cursor says nothing can be resumed from this block.
            var written = MftBlockScan.WriteToBlock(block, new BlockStamp(default, () => DateTime.UtcNow), records,
                MftBlockRowFilter.Full, new BlockWriteReporting(TransferProgress(request), null), cancellationToken);

            var produced = new MftBlockProduceResult(block, JournalIdentifier: 0, NextUsn: 0,
                SkippedRecordCount: MftBlockScan.SaturateSkippedRecordCount(written.SkippedRecordCount));
            block = null;
            return produced;
        }
        finally
        {
            block?.Dispose();
        }
    }

    // The batch already read, then the rest.
    static IEnumerable<IReadOnlyList<MftRecord>> Remaining(IEnumerator<IReadOnlyList<MftRecord>> batches,
        bool hasBatch)
    {
        while (hasBatch)
        {
            yield return batches.Current;
            hasBatch = batches.MoveNext();
        }
    }

    // Both adapters deliver on the reporting thread, so the parse total is seen before the
    // transfer phase reports its smaller count of written rows.
    static DirectProgress<MftScanProgress>? ParseProgress(MftBlockProduceRequest request)
    {
        return request.Progress is { } progress
            ? new DirectProgress<MftScanProgress>(sample => progress.Report(
                new IndexScanProgress(request.Drive, IndexScanPhase.ParsingMft, ClampRows(sample.RecordsScanned))
                {
                    ExpectedRowCount = ClampRows(sample.TotalRecords)
                }))
            : null;
    }

    static DirectProgress<BlockWriteProgress>? TransferProgress(MftBlockProduceRequest request)
    {
        return request.Progress is { } progress
            ? new DirectProgress<BlockWriteProgress>(sample => progress.Report(
                new IndexScanProgress(request.Drive, IndexScanPhase.Transferring,
                    ClampRows(sample.RecordsProcessed))))
            : null;
    }

    static uint ClampRows(long rows)
    {
        return (uint)Math.Clamp(rows, 0, uint.MaxValue);
    }
}
