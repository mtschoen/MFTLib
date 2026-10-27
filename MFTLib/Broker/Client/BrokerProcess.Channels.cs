using System.Buffers;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Opens a volume pipe for one operation through the control pipe's channel handshake.</summary>
sealed partial class BrokerProcess
{
    // One owned operation: create the pipe server, register the request, write OpenChannel, then
    // take the host's connection and the ChannelOpened reply in either order, then write the
    // operation's one request frame. Every failed branch releases the pipe server and anything
    // connected to it, so the host reads EOF on whatever end it holds and its own bounded waits
    // end the rest.
    internal async Task<BrokerVolumeChannel> OpenChannelAsync(VolumeKey volume,
        Action<ArrayBufferWriter<byte>> writeFirstRequest, CancellationToken cancellationToken)
    {
        volume = volume.Validated();
        var pipe = _pipes.Listen(BrokerFrame.PipeNamePrefix + Guid.NewGuid().ToString("N"));
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _lifetime.Token);
        var connection = pipe.WaitForConnectionAsync(connectCancellation.Token);
        try
        {
            await RequestChannelAsync(volume, pipe.PipeName, cancellationToken).ConfigureAwait(false);
            var stream = await AwaitConnectionAsync(volume, connection, cancellationToken).ConfigureAwait(false);
            var channel = new BrokerVolumeChannel(volume, pipe, stream, pipe.PipeName,
                ForgetChannel, _timeProvider);
            try
            {
                TrackChannel(channel);
                await channel.WriteAsync(writeFirstRequest, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return channel;
        }
        catch
        {
            await connectCancellation.CancelAsync().ConfigureAwait(false);
            await pipe.DisposeAsync().ConfigureAwait(false);
            ObserveAbandoned(connection);
            throw;
        }
    }

    async Task RequestChannelAsync(VolumeKey volume, string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync((writer, requestId) => BrokerProtocol.WriteOpenChannel(writer, BrokerFrame.OpenChannel(requestId, volume, pipeName)),
                BrokerFrameKind.ChannelOpened, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerChannelLostException exception) when (exception.Volume is null)
        {
            throw new BrokerChannelLostException(volume,
                $"Volume {volume} channel could not be opened: {exception.Message}", exception);
        }
    }

    // The host connects before it acknowledges, so after ChannelOpened the connection is due at
    // once; a host that acknowledged and never connected is given ControlReplyTimeout.
    async Task<Stream> AwaitConnectionAsync(VolumeKey volume, Task<Stream> connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.WaitAsync(BrokerLiveness.ControlReplyTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new BrokerChannelLostException(volume,
                $"Volume {volume} channel was opened but did not connect within {BrokerLiveness.FormatSeconds(BrokerLiveness.ControlReplyTimeout)} seconds.",
                exception);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            throw new BrokerChannelLostException(volume,
                $"Volume {volume} channel could not be opened: the broker process ended.");
        }
    }

    // The connection is released with the pipe; its outcome no longer matters, but a fault must
    // not go unobserved.
    static void ObserveAbandoned(Task<Stream> connection)
    {
        connection.ContinueWith(static completed => completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // A channel opened while the process is being disposed would miss disposal's sweep, so it
    // fails instead and its caller's cleanup closes it.
    void TrackChannel(BrokerVolumeChannel channel)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new BrokerChannelLostException(channel.Volume,
                    $"Volume {channel.Volume} channel could not be opened: the broker process was disposed.");
            }

            _channels.Add(channel);
        }
    }

    void ForgetChannel(BrokerVolumeChannel channel)
    {
        lock (_gate)
        {
            _channels.Remove(channel);
        }
    }
}
