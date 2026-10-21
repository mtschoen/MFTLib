using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>Holds an extra reader's cleanup until the writer has requested stop.</summary>
internal sealed class EnumerationDrainObservation : IDisposable
{
    readonly string _root;
    readonly EnumerationReadObservation _reads = new();
    readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationToken _workerToken;
    int _writerThread;

    internal EnumerationDrainObservation(string root)
    {
        _root = root;
        EnumerationWalkLimit.Hooks.DirectoryEntered = Enter;
        EnumerationWalkLimit.Hooks.DirectoryExited = Exit;
    }

    internal TestGate WorkerExit { get; } = new();
    internal Task Stopped => _stopped.Task;
    internal int Active => _reads.Active;
    internal IEnumerable<string> Visited => _reads.Visits.Keys;
    bool IsWriter => Environment.CurrentManagedThreadId == _writerThread;
    internal string? FaultedPath { get; private set; }

    internal Task<EnumerationResult> Start(EnumerationProducer producer, BlockWriter writer,
        IProgress<IndexScanProgress>? progress) =>
        EnumerationWalkTestSupport.ProduceAsync(producer, writer, progress, CancellationToken.None,
            maximumWorkers: 2, starting: () => _writerThread = Environment.CurrentManagedThreadId);

    internal Action<string, int> FailWriterRead(Exception failure) => (path, _) =>
    {
        if (path != _root && IsWriter)
        {
            FaultedPath = path;
            throw failure;
        }
    };

    void Enter(DriveKey drive, string path, CancellationToken token)
    {
        _reads.Enter(drive, path, token);
        if (path == _root)
        {
            return;
        }

        if (IsWriter)
        {
            TestGate.WaitSynchronously(WorkerExit.Entered);
        }
        else
        {
            _workerToken = token;
        }
    }

    void Exit(DriveKey drive, string path)
    {
        try
        {
            if (path != _root && !IsWriter)
            {
                using var registration = _workerToken.Register(() => _stopped.TrySetResult());
                WorkerExit.MarkEntered();
                WorkerExit.WaitForRelease();
            }
        }
        finally
        {
            _reads.Exit(drive, path);
        }
    }

    public void Dispose()
    {
        WorkerExit.Release();
        EnumerationWalkLimit.Hooks.ResetToDefaults();
    }
}
