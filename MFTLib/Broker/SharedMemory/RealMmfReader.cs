using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
///     Production <see cref="IMmfReader" />: opens the UI-pre-created, page-file-backed
///     map by name and reads exactly the bytes the broker wrote (as reported in the
///     <c>ScanReady</c> frame). Mirrors <see cref="RealMmfWriter" /> in reverse.
///     Named memory-mapped files are a Windows facility; the client only uses them on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealMmfReader : IStreamingMmfReader
{
    public ScanRecord[] Read(string mmfName, long byteLength)
    {
        var list = new List<ScanRecord>();
        foreach (var batch in ReadBatches(mmfName, byteLength, 4096, CancellationToken.None))
        {
            list.AddRange(batch);
        }

        return list.ToArray();
    }

    public IEnumerable<ScanRecord[]> ReadBatches(
        string mmfName, long byteLength, int batchSize,
        CancellationToken cancellationToken)
    {
        using var map = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Read);
        using var view = map.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Read);
        foreach (var batch in ScanPayload.ReadBatches(view, byteLength, batchSize, cancellationToken))
        {
            yield return batch;
        }
    }
}
