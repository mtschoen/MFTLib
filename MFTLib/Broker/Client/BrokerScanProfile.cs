namespace MFTLib;

/// <summary>
///     Selects which cold-scan records the journal broker returns. The default preserves
///     the complete MFT inventory; directory-index mode keeps only directory records
///     (for path resolution) plus any caller-named files.
/// </summary>
public enum BrokerScanProfile
{
    /// <summary>Returns the complete MFT inventory.</summary>
    Full = 0,
    /// <summary>
    ///     Returns directory records for path resolution and records whose names occur in
    ///     <see cref="BrokerScanOptions.KeepFileNames" />.
    /// </summary>
    DirectoryIndex = 1
}
