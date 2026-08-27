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
    ///     Ignored when <see cref="MmfCapacityPlanner" /> is set.
    /// </summary>
    public long? MmfCapacityBytes { get; init; }

    /// <summary>
    ///     Per-drive map-capacity planner: given a drive letter and its queried
    ///     <see cref="NtfsVolumeInformation" /> (null when the volume query failed, or was
    ///     not attempted, for that drive), returns the map capacity in bytes to request for
    ///     that drive. When set, <see cref="JournalBrokerClient.ArmScanAndCatchUpAsync" />
    ///     queries every drive's volume information from the broker first (one extra round
    ///     trip before the scan begins) and calls this delegate per drive instead of using
    ///     <see cref="MmfCapacityBytes" /> or the 2 GiB default. See
    ///     <see cref="JournalBrokerClient.DefaultCapacityPlanner" /> for a ready-made
    ///     record-count-based planner. Must return a positive value. Like
    ///     <see cref="MmfCapacityBytes" />, this option is not persisted across parameterless
    ///     <see cref="JournalBrokerScanSession.RescanAsync(CancellationToken)" /> calls;
    ///     supply options explicitly to rescans if a planner is needed.
    /// </summary>
    public Func<string, NtfsVolumeInformation?, long>? MmfCapacityPlanner { get; init; }
}
