using System.Globalization;

namespace MFTLib;

/// <summary>Opens one drive pipe and serves its one operation.</summary>
internal sealed partial class JournalBrokerHost
{
    // One drive pipe and what its operation needs to write to it. The pipe writer's lock keeps a
    // scan's progress pump, its operation and the heartbeat sender from interleaving frames on
    // this pipe only; the pipe writer is also the operation state the channel's source reports to.
    sealed class DriveChannel(Stream stream, string drive, HostPipeWriter pipe)
    {
        public Stream Stream { get; } = stream;
        public string Drive { get; } = drive;
        public string Tag => Pipe.Tag;
        public HostPipeWriter Pipe { get; } = pipe;
    }

    static Task WriteChannelErrorAsync(DriveChannel channel, string message, CancellationToken cancellationToken)
    {
        return channel.Pipe.WriteFrameAsync(writer => BrokerProtocol.WriteError(writer, 0, message),
            cancellationToken);
    }

    // Connects the pipe the client named, bounded by ChannelConnectTimeout on the host's clock.
    // On success the reply is ChannelOpened and the channel is served on its own tracked task; on
    // failure or timeout the reply is Error with the request id, and a connection that arrives
    // after the host stopped waiting for it is disposed.
    async Task OpenChannelAsync(ControlSession session, BrokerFrame request)
    {
        var requestId = request.RequestId;
        if (!BrokerDriveLetter.TryNormalize(request.RequireDrive(), out var drive))
        {
            await WriteControlErrorAsync(session, requestId,
                $"'{request.RequireDrive()}' is not a valid drive letter.").ConfigureAwait(false);
            return;
        }

        var pipeName = request.RequirePipeName();
        if (await ConnectChannelAsync(session, requestId, drive, pipeName).ConfigureAwait(false) is not { } stream)
        {
            return;
        }

        try
        {
            await WriteControlFrameAsync(session, writer => BrokerProtocol.WriteChannelOpened(writer, requestId))
                .ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var tag = BrokerDiagnostics.DriveChannel(drive[0], session.NextChannelSequence(drive[0]));
        session.Tasks.Track(Task.Run(() => ServeChannelAsync(session, stream, drive, tag), CancellationToken.None));
    }

    // Returns the connected stream, or null once the Error reply has been written.
    async Task<Stream?> ConnectChannelAsync(ControlSession session, uint requestId, string drive, string pipeName)
    {
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var connectToken = connectCancellation.Token;
        // Task.Run turns a connector that throws before returning its task into a faulted
        // connection, the same failure as one that faults its task.
        var connection = Task.Run(() => session.ConnectChannel(pipeName, connectToken), CancellationToken.None);
        try
        {
            return await connection.WaitAsync(ChannelConnectTimeout, _timeProvider, session.Token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await connectCancellation.CancelAsync().ConfigureAwait(false);
            DisposeLateConnection(connection);
            var seconds = ChannelConnectTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);
            await WriteControlErrorAsync(session, requestId,
                    $"Drive {drive} channel pipe '{pipeName}' did not connect within {seconds} seconds.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            DisposeLateConnection(connection);
            throw;
        }
        // Anything else, including a connector's own cancellation while the session is alive,
        // is this connection's failure.
        catch (Exception exception)
        {
            await WriteControlErrorAsync(session, requestId,
                    $"Drive {drive} channel pipe '{pipeName}' could not be connected: {exception.Message}")
                .ConfigureAwait(false);
        }

        return null;
    }

    static void DisposeLateConnection(Task<Stream> connection)
    {
        connection.ContinueWith(
            // aislop-ignore-next-line csharp-sync-over-async -- OnlyOnRanToCompletion runs this only on a completed task, so Result never blocks
            completed => completed.Result.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // The channel reads exactly one request frame, bounded by FirstRequestTimeout. Once it has
    // one, a reader task watches for EOF, which cancels the channel's token, while the operation
    // runs; the operation's completion stops and joins the reader, so the channel leaves no task
    // behind. The host closes its end when the operation returns. The heartbeat sender visits the
    // pipe from the first request's wait until the operation has returned; a Stalled frame or a
    // failed heartbeat cancels the channel.
    async Task ServeChannelAsync(ControlSession session, Stream stream, string drive, string tag)
    {
        await using (stream.ConfigureAwait(false))
        {
            using var channelCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            var channelToken = channelCancellation.Token;
            var pipe = new HostPipeWriter(stream, tag, _timeProvider, heartbeatsWhenIdle: false,
                channelCancellation, OperationStatePublishedForTest);
            var channel = new DriveChannel(stream, drive, pipe);
            session.Heartbeats.Register(pipe);
            try
            {
                if (await ReadFirstRequestAsync(channel, channelToken).ConfigureAwait(false) is not { } request)
                {
                    return;
                }

                using var readerStop = CancellationTokenSource.CreateLinkedTokenSource(channelToken);
                var reader = CancelOnEndOfStreamAsync(stream, channelCancellation, readerStop.Token);
                try
                {
                    await RunChannelOperationAsync(channel, request, session.BlockSectionWriter, channelToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await readerStop.CancelAsync().ConfigureAwait(false);
                    await reader.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The client closed the pipe, the session ended, or the operation's source was
                // cancelled: the channel ends quietly, with no terminal frame.
            }
            catch (ClientDisconnectedException)
            {
                // A write found the client's end gone; no frame can reach it, so the channel ends
                // quietly and the other channels are untouched.
            }
            finally
            {
                session.Heartbeats.Unregister(pipe);
                pipe.Close();
            }
        }
    }

    Task RunChannelOperationAsync(DriveChannel channel, BrokerFrame request, IBlockSectionWriter? blockSectionWriter,
        CancellationToken cancellationToken)
    {
        return request.Kind switch
        {
            BrokerFrameKind.ArmAndScan => RunScanAsync(channel, request, blockSectionWriter, cancellationToken),
            BrokerFrameKind.StartWatch => StreamWatchAsync(channel, request.Cursor, cancellationToken),
            _ => WriteChannelErrorAsync(channel,
                $"{request.Kind} is not a drive channel request; expected ArmAndScan or StartWatch",
                cancellationToken)
        };
    }

    // Returns null when no request arrived in time or the pipe closed first; the Error frame, where
    // one can be written, has been written by then.
    async Task<BrokerFrame?> ReadFirstRequestAsync(DriveChannel channel, CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = BrokerFrameStream.ReadFrameAsync(channel.Stream, channel.Tag, readCancellation.Token);
        try
        {
            // The read observes the channel's token itself; this wait bounds only the time.
            return await read.WaitAsync(FirstRequestTimeout, _timeProvider, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await readCancellation.CancelAsync().ConfigureAwait(false);
            await AwaitCancelledReadAsync(read).ConfigureAwait(false);
            var seconds = FirstRequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);
            await WriteChannelErrorAsync(channel,
                    $"Drive {channel.Drive} channel received no request within {seconds} seconds.", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            await WriteChannelErrorAsync(channel,
                    $"Drive {channel.Drive} channel received a malformed request: {exception.Message}",
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    static async Task AwaitCancelledReadAsync(Task<BrokerFrame?> read)
    {
        try
        {
            await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The read was cancelled because the request did not arrive in time.
        }
    }

    // Reads and discards until EOF (the client closed its end) or a read failure, then cancels
    // the channel. The client writes nothing after its one request, so nothing is lost.
    static async Task CancelOnEndOfStreamAsync(Stream stream, CancellationTokenSource channelCancellation,
        CancellationToken stopToken)
    {
        var buffer = new byte[256];
        try
        {
            int read;
            do
            {
                read = await stream.ReadAsync(buffer, stopToken).ConfigureAwait(false);
            } while (read > 0);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // The operation finished first and stopped this reader.
            return;
        }
        catch (IOException)
        {
            // A broken pipe is the client's end gone, the same as EOF.
        }

        await channelCancellation.CancelAsync().ConfigureAwait(false);
    }
}
