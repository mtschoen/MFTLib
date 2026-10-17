using MFTLib.Index;

namespace MFTLib;

/// <summary>Scans one drive into a client-created block section on a channel of its own.</summary>
internal sealed partial class BrokerProcess
{
    /// <summary>
    ///     Scans one drive: sizes its block from the broker's volume query, creates the section,
    ///     and runs the scan on a drive channel of its own. The returned block belongs to the
    ///     caller. A catch-up the journal proved lost still returns the complete block, with
    ///     <see cref="BrokerDriveScanResult.CatchUpLoss" /> set.
    /// </summary>
    /// <param name="driveLetter">The drive to scan.</param>
    /// <param name="target">Where the block file is created, and the identity it carries.</param>
    /// <param name="options">Row filtering for the scan.</param>
    /// <param name="progress">Destination for transfer and parse progress notifications; null reports none.</param>
    /// <param name="cancellationToken">Closes the channel and releases the section.</param>
    /// <exception cref="ArgumentException">
    ///     <see cref="MftScanOptions.RetainedFileNames" /> is too long for one request frame. Nothing is sent.
    /// </exception>
    /// <exception cref="InvalidOperationException">The broker reported the scan failed.</exception>
    /// <exception cref="BrokerChannelLostException">The channel or the process was lost first.</exception>
    /// <exception cref="TimeoutException">The broker did not answer the volume query or the channel open within the reply timeout.</exception>
    internal async Task<BrokerDriveScanResult> ScanDriveAsync(char driveLetter, BlockScanTarget target,
        MftScanOptions options, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        var letter = NormalizeDrive(driveLetter)[0];
        NtfsVolumeInformation volume;
        try
        {
            volume = await QueryVolumeAsync(letter, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerChannelLostException exception) when (exception.Drive is null)
        {
            throw new BrokerChannelLostException(letter,
                $"Drive {letter} scan could not start: {exception.Message}", exception);
        }

        var (sectionName, block, lifetime) = _createBlockSection(letter,
            MftBlockCapacity.CreateOptions(volume, target.Path, target.VolumeSerial, target.DeleteOnClose,
                target.CacheTag));
        var sectionLifetime = new ReleaseOnce(lifetime);
        var section = new DriveBlockSection(sectionName, block, sectionLifetime);
        try
        {
            return await ScanOnChannelAsync(letter, section, options, progress, cancellationToken)
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

    async Task<BrokerDriveScanResult> ScanOnChannelAsync(char letter, DriveBlockSection section,
        MftScanOptions options, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        var frameLength = BrokerProtocol.ArmAndScanFrameLength(section.SectionName, options.RetainedFileNames);
        if (frameLength > BrokerFrameStream.MaximumFrameLength)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"The keep list makes a {frameLength}-byte scan request, over the {BrokerFrameStream.MaximumFrameLength}-byte frame limit."),
                nameof(options));
        }

        var channel = await OpenChannelAsync(letter, writer => BrokerProtocol.WriteArmAndScan(writer, section.SectionName,
            options.RetainedFileNames, options.IncludeFreed), cancellationToken).ConfigureAwait(false);
        await using var ownedChannel = channel.ConfigureAwait(false);
        var collector = new ScanFrames(channel, progress);
        var armed = await collector.ReadArmedCursorAsync(cancellationToken).ConfigureAwait(false);
        var ready = await collector.ReadToScanReadyAsync(cancellationToken).ConfigureAwait(false);

        // The section is written: unpublish its name. The block's own view keeps it mapped.
        section.Lifetime.Release();
        var terminal = await collector.ReadTerminalAsync(cancellationToken).ConfigureAwait(false);
        var outcome = new BlockScanOutcome(section.Block, ready.SkippedRecordCount);
        return terminal.Kind == BrokerFrameKind.ScanCompleted
            ? new BrokerDriveScanResult(letter, armed, null, outcome)
            : new BrokerDriveScanResult(letter, armed, terminal.RequireCatchUpLoss(letter), outcome);
    }

    readonly record struct DriveBlockSection(string SectionName, BlockFile Block, ReleaseOnce Lifetime);

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
    sealed class ScanFrames
    {
        readonly BrokerDriveChannel _channel;
        readonly IProgress<BlockWriteProgress>? _progress;
        readonly char _letter;

        public ScanFrames(BrokerDriveChannel channel, IProgress<BlockWriteProgress>? progress)
        {
            _channel = channel;
            _progress = progress;
            _letter = channel.Drive;
        }

        public async Task<UsnJournalCursor> ReadArmedCursorAsync(CancellationToken cancellationToken)
        {
            var frame = await ReadAsync("Cursor", cancellationToken).ConfigureAwait(false);
            return frame.Kind == BrokerFrameKind.Cursor ? frame.Cursor : throw OutOfOrder(frame, "Cursor");
        }

        public async Task<BrokerFrame> ReadToScanReadyAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var frame = await ReadAsync("ScanReady", cancellationToken).ConfigureAwait(false);
                switch (frame.Kind)
                {
                    case BrokerFrameKind.ScanProgress:
                        if (frame.Progress is { } sample)
                        {
                            _progress?.Report(new BlockWriteProgress(sample.RecordsProcessed, sample.BytesProcessed,
                                sample.TotalRecords, sample.TotalBytes,
                                sample.Phase == BrokerScanPhase.Parsing ? BlockScanPhase.Parsing : BlockScanPhase.Transferring));
                        }

                        break;

                    case BrokerFrameKind.ScanReady:
                        return frame;

                    default:
                        throw OutOfOrder(frame, "ScanProgress or ScanReady");
                }
            }
        }

        public async Task<BrokerFrame> ReadTerminalAsync(CancellationToken cancellationToken)
        {
            var terminal = await ReadAsync("ScanCompleted or CatchUpLost", cancellationToken).ConfigureAwait(false);
            if (terminal.Kind is not (BrokerFrameKind.ScanCompleted or BrokerFrameKind.CatchUpLost))
            {
                throw OutOfOrder(terminal, "ScanCompleted or CatchUpLost");
            }

            // The host closes the channel after its terminal frame; a frame instead is out of order,
            // and so is a truncated or malformed one. The scan is complete once the terminal frame is
            // read, so only this channel's own disposal (the process disposed, say) cutting off the
            // wait for that close leaves the result in place.
            BrokerFrame? extra;
            try
            {
                extra = await ReadSkippingHeartbeatsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (BrokerChannelLostException exception) when (_channel.IsOwnClose(exception))
            {
                return terminal;
            }

            return extra is { } frame ? throw OutOfOrder(frame, $"the channel to close after {terminal.Kind}") : terminal;
        }

        async Task<BrokerFrame> ReadAsync(string expected, CancellationToken cancellationToken)
        {
            var frame = await ReadSkippingHeartbeatsAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new BrokerChannelLostException(_letter,
                            $"Drive {_letter} scan channel closed where {expected} was expected.");
            return frame.Kind == BrokerFrameKind.Error
                ? throw new InvalidOperationException(frame.RequireMessage())
                : frame;
        }

        async Task<BrokerFrame?> ReadSkippingHeartbeatsAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var frame = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame is not { Kind: BrokerFrameKind.Heartbeat })
                {
                    return frame;
                }
            }
        }

        BrokerChannelLostException OutOfOrder(BrokerFrame frame, string expected)
        {
            return new BrokerChannelLostException(_letter,
                $"Drive {_letter} scan channel sent {frame.Kind} where {expected} was expected.");
        }
    }
}
