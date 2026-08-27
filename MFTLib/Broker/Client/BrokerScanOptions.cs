namespace MFTLib;

public sealed record BrokerScanOptions
{
    public BrokerScanProfile Profile { get; init; } = BrokerScanProfile.Full;
    public ScanRecordBatchConsumer? ConsumeRecords { get; init; }
    public IReadOnlyCollection<string>? KeepFileNames { get; init; }
    public IProgress<BrokerScanProgress>? Progress { get; init; }

    /// <summary>
    ///     Per-drive page-file-backed MMF capacity for the cold-scan handoff, or null to use
    ///     <see cref="JournalBrokerClient.DefaultMmfCapacity" />. Windows commits a page-file-backed
    ///     section's full requested capacity to the system commit charge at creation time, even for
    ///     pages that are never touched, so this cost is paid once per drive regardless of how much
    ///     of the map is actually used. Raising it multiplies by however many drives are scanned
    ///     concurrently in one <see cref="JournalBrokerClient.ArmScanAndCatchUpAsync" /> call - size
    ///     it deliberately, and only when the caller knows the machine has the commit-charge headroom
    ///     (or is scanning few enough drives at once) to afford it. Must be positive when set.
    /// </summary>
    public long? MmfCapacityBytes { get; init; }
}
