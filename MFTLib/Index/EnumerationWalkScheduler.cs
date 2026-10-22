namespace MFTLib.Index;

using System.Runtime.ExceptionServices;
using System.Threading.Channels;

/// <summary>
///     Runs directory reads for one walk on worker tasks while the calling thread only commits and
///     dispatches. The walk's base permit is lent to one reader at a time, and further readers borrow
///     only immediately available <see cref="EnumerationWalkLimit" /> permits. A reader returns its
///     permit as soon as its directory is read, so a slow writer never idles a permit. Completed
///     directories wait in a channel holding one per reader slot; outstanding jobs, reading or
///     completed, are capped at twice the slot count, and a publisher that finds the channel full waits.
/// </summary>
sealed class EnumerationWalkScheduler : IDisposable
{
    readonly EnumerationDirectoryReader _reader;
    readonly DriveKey _drive;
    readonly int _maximumReaders;
    readonly int _maximumOutstandingJobs;
    readonly CancellationTokenSource _stopSource;
    readonly Channel<SchedulerCompletion> _channel;
    readonly List<ReadJob> _outstandingJobs = [];
    ExceptionDispatchInfo? _firstDrainFault;
    ExceptionDispatchInfo? _capturedWorkerFault;
    int _readingCount;
    int _baseLent;
    bool _drained;

    internal Action? HandoffWaiting { get; init; }
    internal Action<string>? HandoffPublished { get; init; }

    /// <summary>Creates a scheduler for one walk; no reader runs until <see cref="TryStart" />.</summary>
    /// <param name="reader">Reads one directory into an owned buffer.</param>
    /// <param name="drive">The drive reported to the walk hooks.</param>
    /// <param name="maximumReaders">Concurrent readers for this walk, including the one on the base permit.</param>
    /// <param name="cancellationToken">Stops the walk; joined with the scheduler's own stop.</param>
    public EnumerationWalkScheduler(EnumerationDirectoryReader reader, DriveKey drive,
        int maximumReaders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumReaders, 1);
        _reader = reader;
        _drive = drive;
        _maximumReaders = maximumReaders;
        _maximumOutstandingJobs = 2 * maximumReaders;
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _channel = Channel.CreateBounded<SchedulerCompletion>(new BoundedChannelOptions(maximumReaders)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int OutstandingCount => _outstandingJobs.Count;

    /// <summary>
    ///     True when the walk may use only its base permit. The writer then reads each directory itself
    ///     with <see cref="ReadInline" />, the sequential walk, because no second reader could overlap it.
    /// </summary>
    public bool IsSequential => _maximumReaders == 1;

    /// <summary>
    ///     Starts reading <paramref name="directory" /> on a worker when a reader slot, an outstanding-job
    ///     slot and a permit are all free right now; never waits for any of them.
    /// </summary>
    public bool TryStart(EnumerationDirectoryWork directory)
    {
        if (_stopSource.IsCancellationRequested || _outstandingJobs.Count >= _maximumOutstandingJobs ||
            Volatile.Read(ref _readingCount) >= _maximumReaders)
        {
            return false;
        }

        EnumerationWalkLimit.Lease? extraLease = null;
        if (Interlocked.CompareExchange(ref _baseLent, 1, 0) != 0)
        {
            // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to the job, whose worker releases it after reading
            extraLease = EnumerationWalkLimit.TryEnter();
            if (extraLease is null)
            {
                return false;
            }
        }

        Interlocked.Increment(ref _readingCount);
        var job = new ReadJob(directory, extraLease);
        _outstandingJobs.Add(job);
        job.Task = Task.Run(() => RunJobAsync(job));
        return true;
    }

    async Task RunJobAsync(ReadJob job)
    {
        EnumerationDirectoryBuffer? buffer = null;
        ExceptionDispatchInfo? fault = null;
        try
        {
            buffer = ReadWithHooks(job.Directory, _stopSource.Token);
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
            // Worker observed cooperative stop; completes without an error payload.
        }
        catch (Exception exception)
        {
            fault = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            ReleasePermit(job);
        }

        try
        {
            var completion = new SchedulerCompletion(job, buffer, fault);
            var writing = _channel.Writer.WriteAsync(completion, _stopSource.Token);
            if (!writing.IsCompleted)
            {
                HandoffWaiting?.Invoke();
            }

            await writing.ConfigureAwait(false);
            HandoffPublished?.Invoke(job.Directory.Path);
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
            if (fault is not null)
            {
                RecordCapturedFault(fault);
            }
        }
        catch (Exception exception)
        {
            RecordCapturedFault(ExceptionDispatchInfo.Capture(exception));
        }
    }

    void ReleasePermit(ReadJob job)
    {
        if (job.ExtraLease is { } extraLease)
        {
            extraLease.Dispose();
        }
        else
        {
            Volatile.Write(ref _baseLent, 0);
        }

        Interlocked.Decrement(ref _readingCount);
    }

    void RecordCapturedFault(ExceptionDispatchInfo fault)
    {
        Interlocked.CompareExchange(ref _capturedWorkerFault, fault, null);
    }

    EnumerationDirectoryBuffer ReadWithHooks(EnumerationDirectoryWork directory, CancellationToken token)
    {
        EnumerationWalkLimit.Hooks._directoryEntered?.Invoke(_drive, directory.Path, token);
        try
        {
            return _reader.Read(directory, token);
        }
        finally
        {
            EnumerationWalkLimit.Hooks._directoryExited?.Invoke(_drive, directory.Path);
        }
    }

    /// <summary>Reads a directory on the calling thread under the base permit; only for a sequential walk.</summary>
    public EnumerationDirectoryBuffer ReadInline(EnumerationDirectoryWork directory) =>
        ReadWithHooks(directory, _stopSource.Token);

    /// <summary>Waits for the next completed directory; at least one job must be outstanding.</summary>
    /// <exception cref="InvalidOperationException">No job is outstanding, so nothing could ever complete.</exception>
    public EnumerationDirectoryBuffer Take()
    {
        if (_outstandingJobs.Count == 0)
        {
            throw new InvalidOperationException("No directory read is outstanding to take.");
        }

        // Synchronous single-writer producer blocks until a completion is available
        var completion = _channel.Reader.ReadAsync(_stopSource.Token).AsTask().GetAwaiter().GetResult();
        var job = completion.Job;
        // Publication can precede the publisher's continuation; join before forgetting the job.
        job.Task.GetAwaiter().GetResult();
        _outstandingJobs.Remove(job);

        completion.Fault?.Throw();
        if (completion.Buffer is null)
        {
            _stopSource.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Read job produced no buffer");
        }

        return completion.Buffer;
    }

    public ExceptionDispatchInfo? StopAndDrain()
    {
        if (_drained)
        {
            return _firstDrainFault;
        }

        _drained = true;

        if (!_stopSource.IsCancellationRequested)
        {
            _stopSource.Cancel();
        }

        _channel.Writer.TryComplete();

        foreach (var job in _outstandingJobs)
        {
            try
            {
                // Joins each outstanding worker task during synchronous teardown; each releases its own permit
                job.Task.GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _firstDrainFault ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        _outstandingJobs.Clear();

        while (_channel.Reader.TryRead(out var completion))
        {
            if (completion.Fault is not null)
            {
                _firstDrainFault ??= completion.Fault;
            }
        }

        _firstDrainFault ??= _capturedWorkerFault;
        return _firstDrainFault;
    }

    public void Dispose()
    {
        StopAndDrain();
        _stopSource.Dispose();
    }

    /// <summary>One directory read; <see cref="ExtraLease" /> is null when it reads on the walk's base permit.</summary>
    sealed class ReadJob(EnumerationDirectoryWork directory, EnumerationWalkLimit.Lease? extraLease)
    {
        internal EnumerationDirectoryWork Directory { get; } = directory;
        internal EnumerationWalkLimit.Lease? ExtraLease { get; } = extraLease;
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    readonly struct SchedulerCompletion(ReadJob job, EnumerationDirectoryBuffer? buffer, ExceptionDispatchInfo? fault)
    {
        internal ReadJob Job { get; } = job;
        internal EnumerationDirectoryBuffer? Buffer { get; } = buffer;
        internal ExceptionDispatchInfo? Fault { get; } = fault;
    }
}
