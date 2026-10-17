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
    readonly TimeProvider _timeProvider;
    readonly MftDumpRecordSource? _readRecords;

    /// <summary>Builds a producer for one dump file.</summary>
    /// <param name="dumpFilePath">The full path of the dump, opened anew by each request.</param>
    /// <param name="timeProvider">Supplies the completion time stamped into the block.</param>
    /// <param name="readRecords">Reads the opened input's records; null parses the file.</param>
    internal MftDumpBlockProducer(string dumpFilePath, TimeProvider timeProvider, MftDumpRecordSource? readRecords = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFilePath);
        _dumpFilePath = dumpFilePath;
        _timeProvider = timeProvider;
        _readRecords = readRecords;
    }

    /// <summary>
    ///     Produces the block the request names. The parse and the block write run on a thread-pool thread.
    /// </summary>
    /// <exception cref="IOException">The dump could not be opened.</exception>
    /// <exception cref="InvalidDataException">The dump's content was rejected; the message says which check failed.</exception>
    /// <exception cref="OperationCanceledException">The token stopped the scan.</exception>
    internal Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request,
        CancellationToken cancellationToken) =>
        MftBlockProducerPipeline.RunAsync(request, MftBlockProducerPipeline.SharedAllocator,
            (allowance, token) => Produce(request, allowance, token), cancellationToken);

    MftBlockProduceResult Produce(MftBlockProduceRequest request, ParseThreadAllowance allowance,
        CancellationToken cancellationToken)
    {
        using var input = MftDumpInput.Open(_dumpFilePath);

        // The first batch is read before the block exists: reading it runs the whole parse, so a
        // dump the parser rejects costs no block, whose size follows the dump's length.
        var blockProgress = MftBlockProgressAdapter.Create(request);
        var progress = MftBlockProgressAdapter.CreateParse(blockProgress, null);
        var source = _readRecords is { } readRecords
            ? readRecords(input, progress, cancellationToken)
            : input.ReadRecordBatches(RecordBatchSize, progress, cancellationToken, allowance);
        using var batches = source.GetEnumerator();
        var hasBatch = batches.MoveNext();

        // A dump has no journal: the zero cursor says nothing can be resumed from this block.
        var plan = new MftBlockProducerPipeline.WritePlan(input.VolumeInformation,
            new BlockStamp(default, _timeProvider), new MftScanOptions());
        return MftBlockProducerPipeline.Write(request, plan,
            // ReSharper disable once AccessToDisposedClosure
            (block, _) => new MftDumpRecordValidation(block).Validate(Remaining(batches, hasBatch)),
            blockProgress, cancellationToken);
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
}
