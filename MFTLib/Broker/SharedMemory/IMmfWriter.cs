namespace MFTLib;

public readonly record struct MmfWriteResult(long RecordCount, long ByteLength);

/// <summary>
///     Broker-side seam for writing a cold-scan payload into a shared-memory map.
///     The non-elevated UI pre-creates the page-file-backed <c>MemoryMappedFile</c>
///     (the only safe cross-integrity direction) and passes its name to the elevated
///     broker, which opens it and writes the packed <see cref="ScanPayload" />.
///     Injected so the host is testable without a real map.
/// </summary>
public interface IMmfWriter
{
    /// <summary>
    ///     Open the UI-created map named <paramref name="mmfName" />, write the packed
    ///     scan payload for <paramref name="records" />, and return the number of bytes
    ///     written (the UI reads exactly that many back).
    /// </summary>
    long Write(string mmfName, ScanRecord[] records);
}

/// <summary>
///     Streaming broker-side seam for writing cold-scan record batches directly
///     into a shared-memory map without a full-scan array in RAM.
/// </summary>
public interface IStreamingMmfWriter : IMmfWriter
{
    /// <summary>
    ///     Open the map named <paramref name="mmfName" />, write record batches in
    ///     interleaved payload format v2, and return the record count and byte length.
    /// </summary>
    MmfWriteResult Write(
        string mmfName,
        IEnumerable<IReadOnlyList<ScanRecord>> batches,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Open the map named <paramref name="mmfName" />, write record batches in
    ///     interleaved payload format v2, and return the record count and byte length,
    ///     reporting progress.
    /// </summary>
    MmfWriteResult Write(
        string mmfName,
        IEnumerable<IReadOnlyList<ScanRecord>> batches,
        IProgress<MmfWriteProgress>? progress,
        CancellationToken cancellationToken);
}
