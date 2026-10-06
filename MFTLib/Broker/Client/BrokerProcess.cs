namespace MFTLib;

/// <summary>
///     The elevated broker process of one consumer session, reached through its control pipe.
///     Control requests (volume queries, journal growth, channel opens) share the control pipe
///     under request ids; each drive operation runs on a drive pipe of its own, so a slow or lost
///     drive never delays another. When the control pipe is lost the process has ended: every
///     pending request fails with <see cref="BrokerChannelLostException" /> and <see cref="Ended" />
///     completes with the reason.
/// </summary>
internal sealed partial class BrokerProcess : IAsyncDisposable
{
    /// <summary>
    ///     Bounds a control request frame once it has started writing, a control request's reply once
    ///     the request is written, and a drive pipe's connection once its
    ///     channel-open reply has arrived.
    /// </summary>
    internal static readonly TimeSpan ControlReplyTimeout = BrokerLiveness.ControlReplyTimeout;

    readonly Stream _control;
    readonly IBrokerPipeFactory _pipes;
    readonly BrokerBlockSectionFactory _createBlockSection;
    readonly TimeProvider _timeProvider;
    readonly string _controlPipeName;

    // Cancelled when the process ends or is disposed; stops the control reader and bounds every
    // control write. It is never linked and never given a timer, so it holds nothing to release,
    // and a control write that fails after disposal can still cancel it.
    readonly CancellationTokenSource _lifetime = new();
    readonly Lock _gate = new();
    readonly HashSet<BrokerDriveChannel> _channels = [];
    readonly Task _controlReader;
    int _channelSequence;
    int _disposed;

    // Why the process is ending, recorded before _lifetime is cancelled so the control reader
    // ends it with that reason; null while it runs.
    string? _endRequestedReason;

    readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Builds a process over a connected control pipe. Tests reach it through the harness.</summary>
    /// <param name="control">The connected control pipe.</param>
    /// <param name="pipes">Creates each drive pipe the process opens.</param>
    /// <param name="createBlockSection">Creates the block section each scan writes into.</param>
    /// <param name="timeProvider">The clock of every client write and reply timeout.</param>
    internal BrokerProcess(Stream control, IBrokerPipeFactory pipes, BrokerBlockSectionFactory createBlockSection,
        TimeProvider timeProvider)
        : this(control, pipes, createBlockSection, timeProvider, "mftlib-broker-" + Guid.NewGuid().ToString("N"))
    {
    }

    BrokerProcess(Stream control, IBrokerPipeFactory pipes, BrokerBlockSectionFactory createBlockSection,
        TimeProvider timeProvider, string controlPipeName)
    {
        _control = control;
        _pipes = pipes;
        _createBlockSection = createBlockSection;
        _timeProvider = timeProvider;
        _controlPipeName = controlPipeName;
        _controlReader = Task.Run(ReadControlAsync, CancellationToken.None);
    }

    /// <summary>
    ///     Completes once, from the control pipe's reader, when the process ends for any reason
    ///     (the broker exited or crashed, the control pipe failed, or this process was disposed),
    ///     with the reason. Pending requests have been failed by then and every drive channel reads
    ///     EOF. A caller that looks after the end still gets the reason. The task never faults and
    ///     never stays uncompleted past disposal; its continuations run asynchronously.
    /// </summary>
    public Task<string> Ended => _ended.Task;

    Task? _disposeTask;

    /// <summary>
    ///     Closes the control pipe, which ends every channel on the host, then closes every drive
    ///     channel still open here and waits for the control reader, so <see cref="Ended" /> is
    ///     complete when it returns. Idempotent. How the broker ended never makes it throw: the
    ///     client has already reported that through <see cref="Ended" /> and its failed requests and
    ///     channels, and a control pipe that fails to close is only logged. An exception from
    ///     closing a drive channel still propagates.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = 1;
                _disposeTask = PerformDisposeAsync();
            }
            else if (_disposeTask.IsCompleted)
            {
                return ValueTask.CompletedTask;
            }

            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    async Task PerformDisposeAsync()
    {
        RequestEnd("The broker process was disposed.");
        try
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel,
                $"Closing the control pipe failed: {exception}");
        }

        await CloseChannelsAsync().ConfigureAwait(false);
        await _controlReader.ConfigureAwait(false);
    }

    async Task CloseChannelsAsync()
    {
        BrokerDriveChannel[] open;
        lock (_gate)
        {
            open = _channels.ToArray();
        }

        foreach (var channel in open)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Starts ending the process: the control reader stops at once, fails every pending request
    // with this reason and completes Ended.
    void RequestEnd(string reason)
    {
        lock (_gate)
        {
            _endRequestedReason ??= reason;
        }

        _lifetime.Cancel();
    }

    static string NormalizeDrive(char driveLetter)
    {
        return BrokerDriveLetter.Normalize(driveLetter.ToString());
    }
}
