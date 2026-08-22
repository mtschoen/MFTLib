namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Test <see cref="IStreamingMmfWriter" /> that records writes instead of touching
///     a real memory-mapped file, so broker pipe-loop tests can assert what the host
///     handed off without elevation or a real map.
/// </summary>
public sealed class RecordingMmfWriter : IStreamingMmfWriter
{
    public long LastPayloadRecordCount { get; private set; }
    public string? LastMmfName { get; private set; }
    public List<IReadOnlyList<ScanRecord>> WrittenBatches { get; } = new();

    public long Write(string mmfName, ScanRecord[] records)
    {
        var result = Write(mmfName, new[] { records }, CancellationToken.None);
        return result.ByteLength;
    }

    public MmfWriteResult Write(
        string mmfName, IEnumerable<IReadOnlyList<ScanRecord>> batches,
        CancellationToken cancellationToken)
    {
        LastMmfName = mmfName;
        WrittenBatches.Clear();
        long recordCount = 0;
        long byteLength = ScanPayload.HeaderSize;

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WrittenBatches.Add(batch);
            recordCount += batch.Count;
            foreach (var record in batch)
            {
                byteLength += ScanPayload.FixedRecordHeaderSize +
                              System.Text.Encoding.Unicode.GetByteCount(record.Name) +
                              System.Text.Encoding.Unicode.GetByteCount(record.Path);
            }
        }

        LastPayloadRecordCount = recordCount;
        return new MmfWriteResult(recordCount, byteLength);
    }
}
