using MFTLib.Index;

namespace MFTLib;

/// <summary>The destination of one volume's client-created packed block.</summary>
sealed record BlockScanTarget(string Path, uint VolumeSerial, bool DeleteOnClose)
{
    /// <summary>
    ///     The consumer identity that a later warm-start attempt must match; the broker writes
    ///     it into the header before marking this block complete.
    /// </summary>
    public CacheTag CacheTag { get; init; }

    /// <summary>The scan shape the index requires, written into the header when the section is created.</summary>
    public ulong ScanShape { get; init; }
}
