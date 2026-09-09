using MFTLib.Index;

namespace MFTLib;

/// <summary>Plans and creates per-drive block sections and releases blocks that were not handed to the caller.</summary>
public sealed partial class JournalBrokerClient
{
    static Dictionary<string, BlockScanTarget> ValidateBlockTargets(IReadOnlyList<string> drives, BrokerScanOptions options)
    {
        var targets = new Dictionary<string, BlockScanTarget>(StringComparer.OrdinalIgnoreCase);
        if (options.BlockTargets != null)
        {
            foreach (var pair in options.BlockTargets)
            {
                targets.Add(NormalizeDriveLetter(pair.Key), pair.Value);
            }
        }

        foreach (var drive in drives)
        {
            var letter = NormalizeDriveLetter(drive);
            if (!targets.ContainsKey(letter))
            {
                throw new ArgumentException($"BlockTargets is missing a target for drive {letter}.", nameof(options));
            }
        }

        return targets;
    }

    string PrepareDriveBlock(string letter, BlockScanTarget target,
        IReadOnlyDictionary<string, NtfsVolumeInformation>? volumeInfoByDrive)
    {
        var information = volumeInfoByDrive != null && volumeInfoByDrive.TryGetValue(letter, out var found)
            ? (NtfsVolumeInformation?)found : null;
        var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(information);
        var options = new BlockFileCreateOptions
        {
            Path = target.Path,
            VolumeSerial = target.VolumeSerial,
            DeleteOnClose = target.DeleteOnClose,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity
        };
        var (sectionName, block, lifetime) = createDriveBlockSection(letter, options);
        lock (_mmfLifetimesLock)
        {
            var lifetimeRegistered = false;
            try
            {
                _mmfLifetimes.Add(sectionName, lifetime);
                lifetimeRegistered = true;
                _pendingBlocks.Add(sectionName, block);
            }
            catch
            {
                if (lifetimeRegistered)
                {
                    _mmfLifetimes.Remove(sectionName);
                }

                lifetime.Dispose();
                block.Dispose();
                throw;
            }
        }

        return sectionName;
    }

    BlockFile? TakePendingBlock(string sectionName)
    {
        lock (_mmfLifetimesLock)
        {
            return _pendingBlocks.Remove(sectionName, out var block) ? block : null;
        }
    }

    void ReleasePendingBlocks(IEnumerable<string> sectionNames)
    {
        foreach (var sectionName in sectionNames)
        {
            TakeMmfLifetime(sectionName)?.Dispose();
            TakePendingBlock(sectionName)?.Dispose();
        }
    }
}
