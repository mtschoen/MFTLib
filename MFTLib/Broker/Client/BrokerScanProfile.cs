namespace MFTLib;

/// <summary>
/// Selects which cold-scan records the journal broker returns. The default preserves
/// the complete MFT inventory; directory-index mode keeps only directory records
/// (for path resolution) plus any caller-named files.
/// </summary>
public enum BrokerScanProfile
{
    Full = 0,
    DirectoryIndex = 1,
}
