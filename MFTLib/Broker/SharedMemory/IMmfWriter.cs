namespace MFTLib;

/// <summary>
/// Broker-side seam for writing a cold-scan payload into a shared-memory map.
/// The non-elevated UI pre-creates the page-file-backed <c>MemoryMappedFile</c>
/// (the only safe cross-integrity direction) and passes its name to the elevated
/// broker, which opens it and writes the packed <see cref="ScanPayload"/>.
/// Injected so the host is testable without a real map.
/// </summary>
public interface IMmfWriter
{
    /// <summary>
    /// Open the UI-created map named <paramref name="mmfName"/>, write the packed
    /// scan payload for <paramref name="records"/>, and return the number of bytes
    /// written (the UI reads exactly that many back).
    /// </summary>
    long Write(string mmfName, ScanRecord[] records);
}
