namespace MFTLib;

/// <summary>Configures per-drive block destinations, row filtering, and progress for a cold scan.</summary>
public sealed record BrokerScanOptions
{
    public BrokerScanProfile Profile { get; init; } = BrokerScanProfile.Full;
    /// <summary>Required per-drive block destinations. Keys accept drive paths.</summary>
    public IReadOnlyDictionary<string, BlockScanTarget>? BlockTargets { get; init; }
    public IReadOnlyCollection<string>? KeepFileNames { get; init; }
    public IProgress<BrokerScanProgress>? Progress { get; init; }
}
