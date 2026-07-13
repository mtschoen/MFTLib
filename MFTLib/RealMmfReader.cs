using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
/// Production <see cref="IMmfReader"/>: opens the UI-pre-created, page-file-backed
/// map by name and reads exactly the bytes the broker wrote (as reported in the
/// <c>ScanReady</c> frame). Mirrors <see cref="RealMmfWriter"/> in reverse.
/// Named memory-mapped files are a Windows facility; the client only uses them on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealMmfReader : IMmfReader
{
    public ScanRecord[] Read(string mmfName, long byteLength)
    {
        // Read exactly byteLength bytes - not the generous MMF capacity - so the
        // ScanPayload magic and record count are at the correct offsets.
        var buffer = new byte[byteLength];
        using var map = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Read);
        using var view = map.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Read);
        view.ReadExactly(buffer);
        return ScanPayload.ReadAll(buffer).ToArray();
    }
}
