namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    /// <summary>Arms, writes blocks, and catches up each drive using the requested destinations and profile.</summary>
    public Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        return ArmScanAndCatchUpCoreAsync(drives, options, null, cancellationToken);
    }

    internal Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions options,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        return ArmScanAndCatchUpCoreAsync(drives, options, transmissionStarted, cancellationToken);
    }

    async Task<BrokerScanResult> ArmScanAndCatchUpCoreAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions options,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        var blockTargets = ValidateBlockTargets(drives, options);
        var sectionNamesByDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var volumeQuery = await QueryVolumesAsync(drives, transmissionStarted, cancellationToken).ConfigureAwait(false);
        var collector = new ScanCollector(sectionNamesByDrive, drives.Select(NormalizeDriveLetter), options, TakeMmfLifetime)
        {
            TakePendingBlock = TakePendingBlock
        };
        try
        {
            var drivesSpec = PrepareDriveScan(drives, options, volumeQuery.Volumes, sectionNamesByDrive, blockTargets);
            await WriteFrameAsync(
                writer => BrokerProtocol.WriteArmAndScan(writer, drivesSpec, options.KeepFileNames),
                transmissionStarted, cancellationToken).ConfigureAwait(false);
            while (!collector.IsComplete)
            {
                var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Broker disconnected before block scan and catch-up completed.");
                collector.Apply(frame);
            }

            return collector.ToResult();
        }
        catch
        {
            collector.DisposeBlocks();
            throw;
        }
        finally
        {
            ReleasePendingBlocks(sectionNamesByDrive.Values);
        }
    }

    IDisposable? TakeMmfLifetime(string sectionName)
    {
        lock (_mmfLifetimesLock)
        {
            return _mmfLifetimes.Remove(sectionName, out var lifetime) ? lifetime : null;
        }
    }

    string PrepareDriveScan(
        IReadOnlyList<string> drives, BrokerScanOptions options,
        IReadOnlyDictionary<string, NtfsVolumeInformation> volumeInformationByDrive,
        Dictionary<string, string> sectionNamesByDrive,
        Dictionary<string, BlockScanTarget> blockTargets)
    {
        var profile = options.Profile;
        var specTokens = new List<string>(drives.Count);
        foreach (var drive in drives)
        {
            var letter = NormalizeDriveLetter(drive);
            var sectionName = PrepareDriveBlock(letter, blockTargets[letter], volumeInformationByDrive);
            sectionNamesByDrive[letter] = sectionName;
            specTokens.Add(FormattableString.Invariant($"{letter}:0:0:{sectionName}:{(int)profile}"));
        }

        return string.Join(",", specTokens);
    }
}
