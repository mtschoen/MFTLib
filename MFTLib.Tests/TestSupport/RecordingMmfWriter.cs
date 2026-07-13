namespace MFTLib.Tests.TestSupport;

/// <summary>
/// Test <see cref="IMmfWriter"/> that records the last write instead of touching
/// a real memory-mapped file, so broker pipe-loop tests can assert what the host
/// handed off without elevation or a real map.
/// </summary>
public sealed class RecordingMmfWriter : IMmfWriter
{
    public int LastPayloadRecordCount { get; private set; }
    public string? LastMmfName { get; private set; }

    public long Write(string mmfName, ScanRecord[] records)
    {
        LastMmfName = mmfName;
        LastPayloadRecordCount = records.Length;
        return ScanPayload.ComputeSize(records);
    }
}
