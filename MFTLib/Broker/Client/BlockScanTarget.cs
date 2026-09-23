using MFTLib.Index;

namespace MFTLib;

/// <summary>The destination of one drive's client-created packed block.</summary>
public sealed record BlockScanTarget(string Path, uint VolumeSerial, bool DeleteOnClose)
{
    /// <summary>
    ///     The consumer identity that a later warm-start attempt must match; the broker writes
    ///     it into the header before marking this block complete.
    /// </summary>
    public CacheTag CacheTag { get; init; }
}
