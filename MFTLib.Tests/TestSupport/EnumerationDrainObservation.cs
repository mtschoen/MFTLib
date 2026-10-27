using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Holds the first child-directory reader's cleanup until the writer has requested stop, and lets
///     the second child reader run only once the first is held, so whatever the second reader's
///     directory triggers (a read fault, a writer fault, exhaustion) happens while a sibling is in flight.
/// </summary>
sealed class EnumerationDrainObservation : IDisposable
{
    /// <summary>Readers needed at once: the held one and the active one.</summary>
    internal const int MaximumReaders = 2;

    readonly string _root;
    readonly EnumerationReadObservation _reads = new();
    readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationToken _heldToken;
    int _childReadersEntered;
    string? _heldPath;
    string? _activePath;

    internal EnumerationDrainObservation(string root)
    {
        _root = root;
        EnumerationWalkLimit.Hooks._directoryEntered = Enter;
        EnumerationWalkLimit.Hooks._directoryExited = Exit;
    }

    internal TestGate WorkerExit { get; } = new();
    internal Task Stopped => _stopped.Task;
    internal int Active => _reads.Active;
    internal IEnumerable<string> Visited => _reads.Visits.Keys;
    internal string? FaultedPath { get; private set; }

    internal static Task Start(EnumerationProducer producer, BlockWriter writer,
        IProgress<IndexScanProgress>? progress) =>
        EnumerationWalkTestSupport.ProduceAsync(producer, writer, progress, CancellationToken.None,
            maximumWorkers: MaximumReaders);

    internal Action<string, int> FailActiveRead(Exception failure) => (path, _) =>
    {
        if (path == Volatile.Read(ref _activePath))
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

        if (Interlocked.Increment(ref _childReadersEntered) == 1)
        {
            _heldToken = token;
            Volatile.Write(ref _heldPath, path);
        }
        else
        {
            Volatile.Write(ref _activePath, path);
            TestGate.WaitSynchronously(WorkerExit.Entered);
        }
    }

    void Exit(DriveKey drive, string path)
    {
        try
        {
            if (path == Volatile.Read(ref _heldPath))
            {
                using var registration = _heldToken.Register(() => _stopped.TrySetResult());
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
