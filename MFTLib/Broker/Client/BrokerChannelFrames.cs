namespace MFTLib;

/// <summary>
///     Reads the host's frames on one volume channel for an operation that ends with a terminal frame and then the
///     host closing the channel. Heartbeats may arrive anywhere and are skipped. An <see cref="BrokerFrameKind.Error" />
///     frame fails the operation with the host's message; EOF where a frame was expected, or a frame out of order, is
///     a lost channel.
/// </summary>
/// <param name="channel">The channel the operation runs on.</param>
/// <param name="operation">What the operation is called in a lost-channel message, such as "scan".</param>
sealed class BrokerChannelFrames(BrokerVolumeChannel channel, string operation)
{
    /// <summary>The next frame that is not a heartbeat, which is never an <see cref="BrokerFrameKind.Error" />.</summary>
    /// <exception cref="InvalidOperationException">The host reported an error.</exception>
    /// <exception cref="BrokerChannelLostException">The channel closed before the frame named by <paramref name="expected" />.</exception>
    public async Task<BrokerFrame> ReadAsync(string expected, CancellationToken cancellationToken)
    {
        var frame = await ReadSkippingHeartbeatsAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new BrokerChannelLostException(channel.Volume,
                        $"Volume {channel.Volume} {operation} channel closed where {expected} was expected.");
        return frame.Kind == BrokerFrameKind.Error
            ? throw new InvalidOperationException(frame.RequireMessage())
            : frame;
    }

    /// <summary>
    ///     Reads to the channel's close after <paramref name="terminal" />: the host closes the channel after its
    ///     terminal frame, so a frame instead is out of order, and so is a truncated or malformed one. The operation is
    ///     complete once the terminal frame is read, so only this channel's own disposal (the process disposed, say)
    ///     cutting off the wait for that close returns normally.
    /// </summary>
    public async Task ReadClosureAsync(BrokerFrame terminal, CancellationToken cancellationToken)
    {
        BrokerFrame? extra;
        try
        {
            extra = await ReadSkippingHeartbeatsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerChannelLostException exception) when (channel.IsOwnClose(exception))
        {
            return;
        }

        if (extra is { } frame)
        {
            throw OutOfOrder(frame, $"the channel to close after {terminal.Kind}");
        }
    }

    public BrokerChannelLostException OutOfOrder(BrokerFrame frame, string expected)
    {
        return new BrokerChannelLostException(channel.Volume,
            $"Volume {channel.Volume} {operation} channel sent {frame.Kind} where {expected} was expected.");
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
}
