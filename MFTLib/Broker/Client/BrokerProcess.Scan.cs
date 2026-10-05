using MFTLib.Index;

namespace MFTLib;

/// <summary>Scans one drive into a client-created block section on a channel of its own.</summary>
public sealed partial class BrokerProcess
{
    /// <summary>
    ///     Scans one drive: sizes its block from the broker's volume query, creates the section,
    ///     and runs the scan on a drive channel of its own. The returned block belongs to the
    ///     caller. A catch-up the journal proved lost still returns the complete block, with
    ///     <see cref="BrokerDriveScanResult.CatchUpLoss" /> set.
    /// </summary>
    /// <param name="driveLetter">The drive to scan.</param>
    /// <param name="target">Where the block file is created, and the identity it carries.</param>
    /// <param name="options">Row filtering and progress for the scan.</param>
    /// <param name="cancellationToken">Closes the channel and releases the section.</param>
    /// <exception cref="ArgumentException">
    ///     <see cref="BrokerScanOptions.KeepFileNames" /> is too long for one request frame. Nothing is sent.
    /// </exception>
    /// <exception cref="InvalidOperationException">The broker reported the scan failed.</exception>
    /// <exception cref="BrokerChannelLostException">The channel or the process was lost first.</exception>
    /// <exception cref="TimeoutException">The broker did not answer the volume query or the channel open within the reply timeout.</exception>
    internal async Task<BrokerDriveScanResult> ScanDriveAsync(char driveLetter, BlockScanTarget target,
        BrokerScanOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        var letter = NormalizeDrive(driveLetter)[0];
        NtfsVolumeInformation volume;
        try
        {
            volume = await QueryVolumeAsync(letter, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerChannelLostException exception) when (exception.DriveLetter is null)
        {
            throw new BrokerChannelLostException(letter,
                $"Drive {letter} scan could not start: {exception.Message}", exception);
        }

        var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(volume);
        var (sectionName, block, lifetime) = _createBlockSection(letter, new BlockFileCreateOptions
        {
            Path = target.Path,
            VolumeSerial = target.VolumeSerial,
            DeleteOnClose = target.DeleteOnClose,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity,
            CacheTag = target.CacheTag
        });
        var sectionLifetime = new ReleaseOnce(lifetime);
        try
        {
            return await ScanOnChannelAsync(letter, sectionName, block, sectionLifetime, options, cancellationToken)
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

    async Task<BrokerDriveScanResult> ScanOnChannelAsync(char letter, string sectionName, BlockFile block,
        ReleaseOnce sectionLifetime, BrokerScanOptions options, CancellationToken cancellationToken)
    {
        var frameLength = BrokerProtocol.ArmAndScanFrameLength(sectionName, options.KeepFileNames);
        if (frameLength > BrokerFrameStream.MaximumFrameLength)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"The keep list makes a {frameLength}-byte scan request, over the {BrokerFrameStream.MaximumFrameLength}-byte frame limit."),
                nameof(options));
        }

        var channel = await OpenChannelAsync(letter, writer => BrokerProtocol.WriteArmAndScan(writer, sectionName,
            options.Profile, options.KeepFileNames), cancellationToken).ConfigureAwait(false);
        await using var ownedChannel = channel.ConfigureAwait(false);
        var collector = new ScanFrames(channel, options.Progress);
        var armed = await collector.ReadArmedCursorAsync(cancellationToken).ConfigureAwait(false);
        var ready = await collector.ReadToScanReadyAsync(cancellationToken).ConfigureAwait(false);

        // The section is written: unpublish its name. The block's own view keeps it mapped.
        sectionLifetime.Release();
        var terminal = await collector.ReadTerminalAsync(cancellationToken).ConfigureAwait(false);
        var outcome = new BlockScanOutcome(block, ready.SkippedRecordCount);
        return terminal.Kind == BrokerFrameKind.ScanCompleted
            ? new BrokerDriveScanResult(letter, armed, terminal.Cursor, null, outcome)
            : new BrokerDriveScanResult(letter, armed, null, terminal.RequireCatchUpLoss(letter), outcome);
    }

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
    sealed class ScanFrames(BrokerDriveChannel channel, IProgress<BrokerScanProgress>? progress)
    {
        readonly char _letter = channel.DriveLetter;

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
                            progress?.Report(sample with { DriveLetter = _letter.ToString() });
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
            catch (BrokerChannelLostException exception) when (channel.IsOwnClose(exception))
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
                var frame = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
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
