using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
///     Production <see cref="IMmfWriter" />: opens the UI-created, page-file-backed
///     map by name and writes the packed <see cref="ScanPayload" /> into it. Named
///     memory-mapped files are a Windows facility; the broker only runs on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealMmfWriter : IStreamingMmfWriter
{
    public long Write(string mmfName, ScanRecord[] records)
    {
        var result = Write(mmfName, new[] { records }, CancellationToken.None);
        return result.ByteLength;
    }

    public MmfWriteResult Write(
        string mmfName, IEnumerable<IReadOnlyList<ScanRecord>> batches,
        CancellationToken cancellationToken)
    {
        return Write(mmfName, batches, null, cancellationToken);
    }

    public MmfWriteResult Write(
        string mmfName, IEnumerable<IReadOnlyList<ScanRecord>> batches,
        IProgress<MmfWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var map = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Write);
        using var view = map.CreateViewStream(0, 0, MemoryMappedFileAccess.Write);
        return ScanPayload.Write(view, batches, progress, cancellationToken);
    }
}

