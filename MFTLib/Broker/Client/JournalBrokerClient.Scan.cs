namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    /// <summary>
    ///     Arms, scans, and catches up each drive with optional scan profile, consumer, marker files, and progress callback.
    /// </summary>
    public Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ArmScanAndCatchUpCoreAsync(drives, options, null, cancellationToken);
    }

    internal Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions? options,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        return ArmScanAndCatchUpCoreAsync(drives, options, transmissionStarted, cancellationToken);
    }

    async Task<BrokerScanResult> ArmScanAndCatchUpCoreAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions? options,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        var profile = options?.Profile ?? BrokerScanProfile.Full;
        var keepFileNames = options?.KeepFileNames;
        var planner = options?.MmfCapacityPlanner;
        var mmfCapacityBytes = options?.MmfCapacityBytes ?? DefaultMmfCapacity;
        var mmfNamesByDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // A planner needs each drive's volume information before any map can be sized, so
        // this round trip (a separate request/response exchange over the same pipe) must
        // complete before ArmAndScan is written. A drive the query fails for is not in
        // volumeInfoByDrive, and the planner receives null for it below.
        IReadOnlyDictionary<string, NtfsVolumeInformation>? volumeInfoByDrive = null;
        if (planner != null)
        {
            var volumeQuery = await QueryVolumesAsync(drives, transmissionStarted, cancellationToken).ConfigureAwait(false);
            volumeInfoByDrive = volumeQuery.Volumes;
        }

        var drivesSpec = PrepareDriveScan(
            drives, profile, mmfCapacityBytes, planner, volumeInfoByDrive, mmfNamesByDrive);

        await WriteFrameAsync(
            writer => BrokerProtocol.WriteArmAndScan(writer, drivesSpec, keepFileNames),
            transmissionStarted, cancellationToken).ConfigureAwait(false);

        var collector = new ScanCollector(
            mmfReader, mmfNamesByDrive, drives.Select(NormalizeDriveLetter),
            options, TakeMmfLifetime, cancellationToken);

        while (!collector.IsComplete)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                break;
            }

            await collector.ApplyAsync(frame.Value).ConfigureAwait(false);
        }

        return collector.ToResult();
    }

    IDisposable? TakeMmfLifetime(string mmfName)
    {
        lock (_mmfLifetimesLock)
        {
            return _mmfLifetimes.Remove(mmfName, out var lifetime) ? lifetime : null;
        }
    }

    string PrepareDriveScan(
        IReadOnlyList<string> drives, BrokerScanProfile profile, long mmfCapacityBytes,
        Func<string, NtfsVolumeInformation?, long>? planner,
        IReadOnlyDictionary<string, NtfsVolumeInformation>? volumeInfoByDrive,
        Dictionary<string, string> mmfNamesByDrive)
    {
        // mmfCapacityBytes is the fallback for every drive when no planner is set, so it
        // is validated eagerly; a planner's per-drive result is validated as each drive is
        // sized below instead, since mmfCapacityBytes itself is unused in that mode.
        if (planner == null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(mmfCapacityBytes, 0);
        }

        var specTokens = new List<string>(drives.Count);
        foreach (var drive in drives)
        {
            var letter = NormalizeDriveLetter(drive);
            var capacity = mmfCapacityBytes;
            if (planner != null)
            {
                var info = volumeInfoByDrive != null && volumeInfoByDrive.TryGetValue(letter, out var found)
                    ? (NtfsVolumeInformation?)found
                    : null;
                capacity = planner(letter, info);
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
            }

            var (mmfName, lifetime) = createDriveMmf(letter, capacity);
            lock (_mmfLifetimesLock)
            {
                _mmfLifetimes[mmfName] = lifetime;
            }

            mmfNamesByDrive[letter] = mmfName;
            specTokens.Add(FormattableString.Invariant($"{letter}:0:0:{mmfName}:{(int)profile}"));
        }

        return string.Join(",", specTokens);
    }
}
