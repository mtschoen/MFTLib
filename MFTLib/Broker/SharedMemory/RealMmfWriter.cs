using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
/// Production <see cref="IMmfWriter"/>: opens the UI-created, page-file-backed
/// map by name and writes the packed <see cref="ScanPayload"/> into it. Named
/// memory-mapped files are a Windows facility; the broker only runs on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealMmfWriter : IMmfWriter
{
    public long Write(string mmfName, ScanRecord[] records)
    {
        var byteLength = ScanPayload.ComputeSize(records);
        // Pack into a transient buffer, then copy into the shared view. The MMF's
        // value is avoiding the disk round-trip, not this RAM copy; a zero-copy
        // span write would need unsafe and is a later optimization.
        var buffer = new byte[byteLength];
        ScanPayload.Write(buffer, records);

        using var map = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Write);
        using var view = map.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Write);
        view.Write(buffer, 0, buffer.Length);
        return byteLength;
    }
}
