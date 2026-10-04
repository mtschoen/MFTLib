namespace MFTLib;

/// <summary>Configures row filtering and progress for a cold scan.</summary>
public sealed record BrokerScanOptions
{
    /// <summary>
    ///     Selects whether the broker returns every MFT record or only records needed for a
    ///     directory index. The default is <see cref="BrokerScanProfile.Full" />.
    /// </summary>
    public BrokerScanProfile Profile { get; init; } = BrokerScanProfile.Full;
    /// <summary>
    ///     Names to retain when <see cref="Profile" /> is
    ///     <see cref="BrokerScanProfile.DirectoryIndex" />. A null collection retains no
    ///     additional non-directory records.
    /// </summary>
    public IReadOnlyCollection<string>? KeepFileNames { get; init; }
    /// <summary>
    ///     Receives progress reported while the broker performs the cold scan, or null when
    ///     no progress notifications are required.
    /// </summary>
    public IProgress<BrokerScanProgress>? Progress { get; init; }
}
