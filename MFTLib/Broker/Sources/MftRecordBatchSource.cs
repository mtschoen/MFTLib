namespace MFTLib;

/// <summary>Streams MFT columns in bounded batches for the packed block output path.</summary>
public delegate IEnumerable<IReadOnlyList<MftRecord>> MftRecordBatchSource(
    string driveLetter, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken);
