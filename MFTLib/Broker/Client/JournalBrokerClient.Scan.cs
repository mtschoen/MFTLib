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

    Task<BrokerScanResult> ArmScanAndCatchUpCoreAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions options,
        Action? transmissionStarted,
        CancellationToken cancellationToken) =>
        RunControlExchangeAsync((exchange, token) =>
            ArmScanExchangeAsync(drives, options, transmissionStarted, exchange, token), cancellationToken);

    async Task<BrokerScanResult> ArmScanExchangeAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions options,
        Action? transmissionStarted,
        ControlExchange exchange,
        CancellationToken cancellationToken)
    {
        var blockTargets = ValidateBlockTargets(drives, options);
        var sectionNamesByDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var volumeQuery = await QueryVolumesCoreAsync(drives, transmissionStarted, exchange, cancellationToken).ConfigureAwait(false);
        var collector = new ScanCollector(sectionNamesByDrive, drives.Select(NormalizeDriveLetter), options, TakeMmfLifetime)
        {
            TakePendingBlock = TakePendingBlock
        };
        try
        {
            var drivesSpec = PrepareDriveScan(drives, options, volumeQuery.Volumes, sectionNamesByDrive, blockTargets);
            var expectedDrives = drives.Select(NormalizeDriveLetter).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedSections = sectionNamesByDrive.Values.ToHashSet(StringComparer.Ordinal);
            ExpectControlReplies(exchange, frame => frame.Kind switch
            {
                BrokerFrameKind.ScanReady => expectedSections.Contains(frame.RequireMmfName()),
                BrokerFrameKind.Cursor or BrokerFrameKind.ScanProgress or BrokerFrameKind.Warning =>
                    expectedDrives.Contains(frame.RequireDrive()),
                BrokerFrameKind.JournalBatch or BrokerFrameKind.Error =>
                    frame.ArmEpoch == BrokerFrame.NoArmEpoch && expectedDrives.Contains(frame.RequireDrive()),
                _ => false
            });
            await WriteFrameAsync(
                writer => BrokerProtocol.WriteArmAndScan(writer, drivesSpec, options.KeepFileNames),
                () => { exchange.RequestInFlight = true; transmissionStarted?.Invoke(); },
                cancellationToken).ConfigureAwait(false);
            while (!collector.IsComplete)
            {
                var frame = await ReadControlFrameAsync(exchange, cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Broker disconnected before block scan and catch-up completed.");
                collector.Apply(frame);
            }

            exchange.RequestInFlight = false;
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
