namespace MFTLib.Index;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

internal sealed class EnumerationWalkScheduler : IDisposable
{
    readonly EnumerationDirectoryReader _reader;
    readonly DriveKey _drive;
    readonly int _maximumExtraJobs;
    readonly CancellationTokenSource _stopSource;
    readonly Channel<SchedulerCompletion> _channel;
    readonly List<ExtraJob> _outstandingJobs = [];
    ExceptionDispatchInfo? _firstDrainFault;
    ExceptionDispatchInfo? _capturedWorkerFault;
    bool _drained;

    internal Action? HandoffWaiting { get; init; }

    public EnumerationWalkScheduler(EnumerationDirectoryReader reader, DriveKey drive,
        int maximumWorkers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _drive = drive;
        _maximumExtraJobs = Math.Max(0, maximumWorkers - 1);
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _channel = Channel.CreateBounded<SchedulerCompletion>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int OutstandingCount => _outstandingJobs.Count;

    public bool TryStart(EnumerationDirectoryWork directory)
    {
        if (_stopSource.IsCancellationRequested || _outstandingJobs.Count >= _maximumExtraJobs)
        {
            return false;
        }

        // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to ExtraJob which is disposed on completion or drain
        var lease = EnumerationWalkLimit.TryEnter();
        if (lease is null)
        {
            return false;
        }

        var job = new ExtraJob(directory, lease);
        _outstandingJobs.Add(job);
        job.Task = Task.Run(() => RunJobAsync(job));
        return true;
    }

    async Task RunJobAsync(ExtraJob job)
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

        try
        {
            var completion = new SchedulerCompletion(job, buffer, fault);
            var writing = _channel.Writer.WriteAsync(completion, _stopSource.Token);
            if (!writing.IsCompleted)
            {
                HandoffWaiting?.Invoke();
            }

            await writing.ConfigureAwait(false);
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

    void RecordCapturedFault(ExceptionDispatchInfo fault)
    {
        Interlocked.CompareExchange(ref _capturedWorkerFault, fault, null);
    }

    EnumerationDirectoryBuffer ReadWithHooks(EnumerationDirectoryWork directory, CancellationToken token)
    {
        EnumerationWalkLimit.Hooks.DirectoryEntered?.Invoke(_drive, directory.Path, token);
        try
        {
            return _reader.Read(directory, token);
        }
        finally
        {
            EnumerationWalkLimit.Hooks.DirectoryExited?.Invoke(_drive, directory.Path);
        }
    }

    public EnumerationDirectoryBuffer ReadInline(EnumerationDirectoryWork directory) =>
        ReadWithHooks(directory, _stopSource.Token);

    public bool TryTake([NotNullWhen(true)] out EnumerationDirectoryBuffer? result)
    {
        result = null;
        if (_channel.Reader.TryRead(out var completion))
        {
            result = FinishJob(completion);
            return result is not null;
        }

        return false;
    }

    public EnumerationDirectoryBuffer Take()
    {
        // Synchronous single-writer producer blocks until a completion is available
        var completion = _channel.Reader.ReadAsync(_stopSource.Token).AsTask().GetAwaiter().GetResult();
        var result = FinishJob(completion);
        if (result is null)
        {
            _stopSource.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Extra job produced no buffer");
        }

        return result;
    }

    EnumerationDirectoryBuffer? FinishJob(SchedulerCompletion completion)
    {
        var job = completion.Job;
        // Publication can precede the publisher's continuation; join before releasing its permit.
        job.Task.GetAwaiter().GetResult();
        _outstandingJobs.Remove(job);
        job.Lease.Dispose();

        if (completion.Fault is not null)
        {
            completion.Fault.Throw();
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
                // Joins each outstanding worker task during synchronous teardown
                job.Task.GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _firstDrainFault ??= ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                try
                {
                    job.Lease.Dispose();
                }
                catch (Exception exception)
                {
                    _firstDrainFault ??= ExceptionDispatchInfo.Capture(exception);
                }
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

    sealed class ExtraJob(EnumerationDirectoryWork directory, EnumerationWalkLimit.Lease lease)
    {
        internal EnumerationDirectoryWork Directory { get; } = directory;
        internal EnumerationWalkLimit.Lease Lease { get; } = lease;
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    readonly struct SchedulerCompletion(ExtraJob job, EnumerationDirectoryBuffer? buffer, ExceptionDispatchInfo? fault)
    {
        internal ExtraJob Job { get; } = job;
        internal EnumerationDirectoryBuffer? Buffer { get; } = buffer;
        internal ExceptionDispatchInfo? Fault { get; } = fault;
    }
}
