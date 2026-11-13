using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Reads the records of one opened dump in materialized batches, in place of its streaming parse: a test
///     substitutes batches no file can hold.
/// </summary>
delegate IEnumerable<IReadOnlyList<MftRecord>> MftDumpRecordSource(MftDumpInput input,
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
sealed class MftDumpBlockProducer
{
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
            (grown, allowance, token) => Produce(grown, allowance, token), cancellationToken);

    MftBlockProduceResult Produce(MftBlockProduceRequest request, ParseThreadAllowance allowance,
        CancellationToken cancellationToken)
    {
        using var input = MftDumpInput.Open(_dumpFilePath);

        // The block is created before the parse starts, so its rows are written while later chunks are parsed; a
        // dump the parser rejects leaves the block as that failed scan's scratch space.
        var blockProgress = MftBlockProgressAdapter.Create(request);
        var progress = MftBlockProgressAdapter.CreateParse(blockProgress, null);
        // A dump has no journal: the zero cursor says nothing can be resumed from this block.
        var plan = new MftBlockProducerPipeline.WritePlan(input.VolumeInformation,
            new BlockStamp(default, _timeProvider), new MftScanOptions());
        return MftBlockProducerPipeline.Write(request, plan,
            // ReSharper disable once AccessToDisposedClosure
            () => OpenSource(input, progress, allowance, cancellationToken),
            blockProgress, cancellationToken);
    }

    IMftSliceSource OpenSource(MftDumpInput input, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance allowance, CancellationToken cancellationToken) =>
        _readRecords is { } readRecords
            ? new MftRecordSliceSource(_ => readRecords(input, progress, cancellationToken), MftRowValidation.Dump)
            : input.OpenSliceStream(progress, allowance, cancellationToken);
}
