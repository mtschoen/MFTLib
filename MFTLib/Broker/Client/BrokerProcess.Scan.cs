using MFTLib.Index;

namespace MFTLib;

/// <summary>Scans one volume into a client-created block section on a channel of its own.</summary>
sealed partial class BrokerProcess
{
    /// <summary>
    ///     Scans one volume: sizes its block from the broker's volume query, creates the section,
    ///     and runs the scan on a volume channel of its own. The returned block belongs to the
    ///     caller. A catch-up the journal proved lost still returns the complete block, with
    ///     <see cref="BrokerVolumeScanResult.CatchUpLoss" /> set.
    /// </summary>
    /// <param name="volume">The volume to scan.</param>
    /// <param name="target">Where the block file is created, and the identity it carries.</param>
    /// <param name="options">Freed-record inclusion for the scan.</param>
    /// <param name="progress">Destination for scanning and indexing progress; null reports none.</param>
    /// <param name="cancellationToken">Closes the channel and releases the section.</param>
    /// <exception cref="InvalidOperationException">The broker reported the scan failed.</exception>
    /// <exception cref="BrokerChannelLostException">The channel or the process was lost first.</exception>
    /// <exception cref="TimeoutException">The broker did not answer the volume query or the channel open within the reply timeout.</exception>
    internal async Task<BrokerVolumeScanResult> ScanVolumeAsync(VolumeKey volume, BlockScanTarget target,
        MftScanOptions options, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        volume = volume.Validated();
        NtfsVolumeInformation information;
        try
        {
            information = await QueryVolumeAsync(volume, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerChannelLostException exception) when (exception.Volume is null)
        {
            throw new BrokerChannelLostException(volume,
                $"Volume {volume} scan could not start: {exception.Message}", exception);
        }

        var location = new MftBlockCapacity.FileLocation(volume, target.Path, target.VolumeSerial, target.DeleteOnClose);
        var (sectionName, block, lifetime) = _createBlockSection(volume,
            MftBlockCapacity.CreateOptions(information, location,
                new BlockContentIdentity(target.CacheTag, target.ScanShape), target.MinimumCapacities));
        var sectionLifetime = new ReleaseOnce(lifetime);
        var section = new VolumeBlockSection(sectionName, block, sectionLifetime);
        try
        {
            return await ScanOnChannelAsync(volume, section, options, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            try
            {
                sectionLifetime.Release();
            }
            finally
            {
                block.Dispose();
            }

            throw;
        }
    }

    async Task<BrokerVolumeScanResult> ScanOnChannelAsync(VolumeKey volume, VolumeBlockSection section,
        MftScanOptions options, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        var channel = await OpenChannelAsync(volume, writer => BrokerProtocol.WriteArmAndScan(writer, BrokerFrame.ArmAndScan(section.SectionName, options.IncludeFreed, section.Block.DeletesOnClose, section.Block.Header.VolumeSerial)), cancellationToken).ConfigureAwait(false);

        await using var ownedChannel = channel.ConfigureAwait(false);
        var collector = new ScanFrames(channel, progress);
        var armed = await collector.ReadArmedCursorAsync(cancellationToken).ConfigureAwait(false);
        var ready = await collector.ReadToScanReadyAsync(cancellationToken).ConfigureAwait(false);

        // The section is written: unpublish its name. The block's own view keeps it mapped.
        section.Lifetime.Release();
        var terminal = await collector.ReadTerminalAsync(cancellationToken).ConfigureAwait(false);
        var outcome = new BlockScanOutcome(section.Block, ready.SkippedRecordCount);
        return terminal.Kind == BrokerFrameKind.ScanCompleted
            ? new BrokerVolumeScanResult(volume, armed, null, outcome)
            : new BrokerVolumeScanResult(volume, armed, terminal.RequireCatchUpLoss(volume), outcome);
    }

    readonly record struct VolumeBlockSection(string SectionName, BlockFile Block, ReleaseOnce Lifetime);

    // The flag is set before Dispose runs, so a lifetime whose Dispose throws is not disposed a second time.
    sealed class ReleaseOnce(IDisposable lifetime)
    {
        bool _released;

        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            lifetime.Dispose();
        }
    }

    // Reads one scan channel in its only order: Cursor, ScanProgress*, ScanReady, then ScanCompleted
    // or CatchUpLost, then EOF. Heartbeats may arrive anywhere and are skipped. An Error frame fails
    // the scan with the host's message; any other frame out of order, or EOF before the terminal
    // frame, is a lost channel.
    sealed class ScanFrames(BrokerVolumeChannel channel, IProgress<BlockWriteProgress>? progress)
    {
        readonly BrokerChannelFrames _frames = new(channel, "scan");

        public async Task<UsnJournalCursor> ReadArmedCursorAsync(CancellationToken cancellationToken)
        {
            var frame = await _frames.ReadAsync("Cursor", cancellationToken).ConfigureAwait(false);
            return frame.Kind == BrokerFrameKind.Cursor ? frame.Cursor : throw _frames.OutOfOrder(frame, "Cursor");
        }

        public async Task<BrokerFrame> ReadToScanReadyAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var frame = await _frames.ReadAsync("ScanReady", cancellationToken).ConfigureAwait(false);
                switch (frame.Kind)
                {
                    case BrokerFrameKind.ScanProgress:
                        if (frame.Progress is { } sample)
                        {
                            progress?.Report(sample);
                        }

                        break;

                    case BrokerFrameKind.ScanReady:
                        return frame;

                    default:
                        throw _frames.OutOfOrder(frame, "ScanProgress or ScanReady");
                }
            }
        }

        public async Task<BrokerFrame> ReadTerminalAsync(CancellationToken cancellationToken)
        {
            var terminal = await _frames.ReadAsync("ScanCompleted or CatchUpLost", cancellationToken)
                .ConfigureAwait(false);
            if (terminal.Kind is not (BrokerFrameKind.ScanCompleted or BrokerFrameKind.CatchUpLost))
            {
                throw _frames.OutOfOrder(terminal, "ScanCompleted or CatchUpLost");
            }

            await _frames.ReadClosureAsync(terminal, cancellationToken).ConfigureAwait(false);
            return terminal;
        }
    }
}
