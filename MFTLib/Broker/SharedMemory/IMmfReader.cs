namespace MFTLib;

/// <summary>
///     UI-side seam for reading a cold-scan payload from a shared-memory map the client
///     pre-created. Injected so <see cref="JournalBrokerClient" /> is testable without a
///     real named-MMF or a real elevated broker.
/// </summary>
public interface IMmfReader
{
    /// <summary>
    ///     Open the map named <paramref name="mmfName" /> and read exactly
    ///     <paramref name="byteLength" /> bytes (the length the broker reported in the
    ///     <c>ScanReady</c> frame), then deserialize into <see cref="ScanRecord" />s.
    ///     The caller must not exceed the map's capacity.
    /// </summary>
    ScanRecord[] Read(string mmfName, long byteLength);
}

/// <summary>
///     Streaming UI-side seam for reading cold-scan record batches from a shared-memory map
///     without buffering the entire payload into a single array in RAM.
/// </summary>
public interface IStreamingMmfReader : IMmfReader
{
    /// <summary>
    ///     Read records from the map in batches of size <paramref name="batchSize" />.
    /// </summary>
    IEnumerable<ScanRecord[]> ReadBatches(
        string mmfName,
        long byteLength,
        int batchSize,
        CancellationToken cancellationToken);
}
