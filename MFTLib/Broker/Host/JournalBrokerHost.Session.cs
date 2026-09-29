using System.Buffers;

namespace MFTLib;

/// <summary>The control pipe: reads requests, runs each on its own task, and ends the session.</summary>
public sealed partial class JournalBrokerHost
{
    /// <summary>
    ///     Serve a broker session. Reads request frames from <paramref name="control" /> and runs
    ///     each on its own task, so a slow request never delays another's reply; control writes
    ///     are serialized by a control-only lock. <c>OpenChannel</c> connects the named drive pipe
    ///     through <paramref name="connectChannel" /> and serves one scan or one watch on it, on
    ///     its own task. <c>QueryVolume</c> and <c>GrowUsnJournal</c> answer with one reply or one
    ///     <c>Error</c> carrying the request id.
    ///     The session ends when the control pipe reaches EOF, when a control reply finds the
    ///     pipe gone, or when <paramref name="cancellationToken" /> is cancelled. Ending cancels
    ///     every channel and waits for their tasks up to <see cref="ControlClosedGracePeriod" />
    ///     on the host's clock, then returns even if an operation ignores its cancellation.
    /// </summary>
    /// <param name="control">The connected control pipe.</param>
    /// <param name="connectChannel">Connects a drive pipe the client created and named.</param>
    /// <param name="blockSectionWriter">
    ///     Writes packed rows into the client-created section. Null serves a watch-only session;
    ///     a scan channel on such a session ends with one <c>Error</c> frame.
    /// </param>
    /// <param name="cancellationToken">Ends the session.</param>
    public async Task ServeAsync(Stream control, BrokerChannelConnector connectChannel,
        IBlockSectionWriter? blockSectionWriter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(connectChannel);

        using var session = new ControlSession(control, connectChannel, blockSectionWriter, cancellationToken);
        try
        {
            while (await ReadFrameAsync(control, BrokerDiagnostics.ControlChannel, session.Token)
                       .ConfigureAwait(false) is { } request)
            {
                session.Tasks.Track(HandleControlRequestAsync(session, request));
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            // The session was ended by the caller or by a control reply that found the pipe gone;
            // either way the drain below is what remains to do.
        }
        finally
        {
            await session.EndAsync().ConfigureAwait(false);
            await DrainAsync(session.Tasks).ConfigureAwait(false);
        }
    }

    async Task HandleControlRequestAsync(ControlSession session, BrokerFrame request)
    {
        // Off the read loop at once, so a request that blocks in its source never delays the next
        // request's read or reply.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await AnswerControlRequestAsync(session, request).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            // The session is ending; its drain accounts for this request.
        }
        catch (ClientDisconnectedException)
        {
            // A reply that cannot reach the client ends the whole session: there is nobody left to
            // serve, so every channel is cancelled and the control read loop returns.
            await session.EndAsync().ConfigureAwait(false);
        }
    }

    async Task AnswerControlRequestAsync(ControlSession session, BrokerFrame request)
    {
        try
        {
            switch (request.Kind)
            {
                case BrokerFrameKind.OpenChannel:
                    await OpenChannelAsync(session, request).ConfigureAwait(false);
                    break;

                case BrokerFrameKind.QueryVolume:
                    await HandleQueryVolumeAsync(session, request).ConfigureAwait(false);
                    break;

                case BrokerFrameKind.GrowUsnJournal:
                    await HandleGrowUsnJournalAsync(session, request).ConfigureAwait(false);
                    break;

                default:
                    await WriteControlErrorAsync(session, request.RequestId,
                        $"{request.Kind} is not a control request").ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException exception) when (!session.Token.IsCancellationRequested)
        {
            // A source cancelled on its own while the session is alive: the request failed, and
            // it still gets its one reply.
            await WriteControlErrorAsync(session, request.RequestId, exception.Message).ConfigureAwait(false);
        }
    }

    static Task WriteControlFrameAsync(ControlSession session, Action<ArrayBufferWriter<byte>> write)
    {
        // An ended session writes nothing more; checking first also keeps a request the grace
        // period abandoned off the session's lock once the session is disposed.
        session.Token.ThrowIfCancellationRequested();
        return WriteFrameAsync(session.Control, session.WriteLock, BrokerDiagnostics.ControlChannel, write,
            session.Token);
    }

    static Task WriteControlErrorAsync(ControlSession session, uint requestId, string message)
    {
        return WriteControlFrameAsync(session, writer => BrokerProtocol.WriteError(writer, requestId, message));
    }

    // Waits for every tracked task, including tasks tracked while waiting, until all have ended
    // or the grace period has passed on the host's clock. A task still running then is abandoned:
    // the session is ending and the process exits after ServeAsync returns. A task that faulted
    // rethrows here, because nothing else observes it.
    async Task DrainAsync(SessionTasks tasks)
    {
        using var stopDeadline = new CancellationTokenSource();
        var deadline = Task.Delay(ControlClosedGracePeriod, _timeProvider, stopDeadline.Token);
        try
        {
            while (true)
            {
                var snapshot = tasks.Snapshot();
                var all = Task.WhenAll(snapshot);
                if (await Task.WhenAny(all, deadline).ConfigureAwait(false) != all)
                {
                    return;
                }

                await all.ConfigureAwait(false);
                if (tasks.Snapshot().All(task => task.IsCompleted))
                {
                    return;
                }
            }
        }
        finally
        {
            await stopDeadline.CancelAsync().ConfigureAwait(false);
        }
    }

    // Disposed when ServeAsync returns, possibly while a task the grace period abandoned still
    // holds it. Such a task sees Token cancelled (a token stays readable after its source is
    // disposed), so it reaches neither EndAsync's cancel nor the write lock.
    sealed class ControlSession : IDisposable
    {
        readonly CancellationTokenSource _cancellation;
        readonly Dictionary<char, int> _channelSequences = [];

        public ControlSession(Stream control, BrokerChannelConnector connectChannel,
            IBlockSectionWriter? blockSectionWriter, CancellationToken cancellationToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Token = _cancellation.Token;
            Control = control;
            ConnectChannel = connectChannel;
            BlockSectionWriter = blockSectionWriter;
        }

        public Stream Control { get; }
        public BrokerChannelConnector ConnectChannel { get; }
        public IBlockSectionWriter? BlockSectionWriter { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public SessionTasks Tasks { get; } = new();

        /// <summary>Cancelled when the session ends; every request and channel is linked to it.</summary>
        public CancellationToken Token { get; }

        // The drive's next channel number, for the diagnostics tag of its pipe.
        public int NextChannelSequence(char driveLetter)
        {
            lock (_channelSequences)
            {
                var sequence = _channelSequences.GetValueOrDefault(driveLetter) + 1;
                _channelSequences[driveLetter] = sequence;
                return sequence;
            }
        }

        public Task EndAsync()
        {
            return Token.IsCancellationRequested ? Task.CompletedTask : _cancellation.CancelAsync();
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            WriteLock.Dispose();
        }
    }

    // The session's request and channel tasks. A task that completed successfully is forgotten
    // at the next Track; a faulted one is kept so the drain rethrows it.
    sealed class SessionTasks
    {
        readonly Lock _gate = new();
        readonly List<Task> _tasks = [];

        public void Track(Task task)
        {
            lock (_gate)
            {
                _tasks.RemoveAll(tracked => tracked.IsCompletedSuccessfully);
                _tasks.Add(task);
            }
        }

        public Task[] Snapshot()
        {
            lock (_gate)
            {
                return _tasks.ToArray();
            }
        }
    }
}
