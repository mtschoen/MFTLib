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
        var mmfCapacityBytes = options?.MmfCapacityBytes ?? DefaultMmfCapacity;
        var mmfNamesByDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var drivesSpec = PrepareDriveScan(drives, profile, mmfCapacityBytes, mmfNamesByDrive);

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
        Dictionary<string, string> mmfNamesByDrive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(mmfCapacityBytes, 0);

        var specTokens = new List<string>(drives.Count);
        foreach (var drive in drives)
        {
            var letter = NormalizeDriveLetter(drive);
            var (mmfName, lifetime) = createDriveMmf(letter, mmfCapacityBytes);
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
