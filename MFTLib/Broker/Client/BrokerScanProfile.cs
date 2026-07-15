namespace MFTLib;

/// <summary>
/// Selects which cold-scan records the journal broker returns. The default preserves
/// the complete MFT inventory; directory-index mode keeps only records needed for path
/// resolution and Git worktree discovery.
/// </summary>
public enum BrokerScanProfile
{
    Full = 0,
    DirectoryIndexWithGitPointers = 1,
}
