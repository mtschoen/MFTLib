using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A watch source that reports readiness only when a test says so, so every step of a watch
///     start is ordered by the test rather than by scheduling. Each stream the index starts is one
///     <see cref="Stream" />: the test reaches it through <see cref="NextStreamAsync" />, and until
///     it calls <see cref="Stream.ReportReady" /> the source rejects arm and disarm exactly as a
///     source that has not finished starting does. Only the readiness overload is meant to be
///     called; the plain overload throws, so a regression that bypasses readiness fails loudly.
/// </summary>
internal sealed class ReadinessScriptedWatchSource : IIndexWatchSource
{
    readonly Lock _stateLock = new();
    readonly Channel<Stream> _startedStreams = Channel.CreateUnbounded<Stream>();
    readonly List<string> _watchOperations = [];
    Stream? _current;

    /// <summary>Every accepted per-drive call in arrival order, and every rejected one marked so.</summary>
    public IReadOnlyList<string> WatchOperations
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _watchOperations];
            }
        }
    }

    public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("The index must start this source through its readiness overload.");
    }

    public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
        Action reportStreamReady, CancellationToken cancellationToken)
    {
        return StreamAsync(targets, reportStreamReady, cancellationToken);
    }

    public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        RecordOrReject($"arm:{char.ToUpperInvariant(target.DriveLetter)}");
        return Task.CompletedTask;
    }

    public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken)
    {
        RecordOrReject($"disarm:{char.ToUpperInvariant(driveLetter)}");
        return Task.CompletedTask;
    }

    /// <summary>The next stream the index starts, in start order, once its iterator is running.</summary>
    public Task<Stream> NextStreamAsync(CancellationToken cancellationToken)
    {
        return _startedStreams.Reader.ReadAsync(cancellationToken).AsTask()
            .WaitAsync(FakeIndexWatchSource.HangGuard, cancellationToken);
    }

    void RecordOrReject(string operation)
    {
        lock (_stateLock)
        {
            if (_current is not { IsReady: true })
            {
                _watchOperations.Add($"rejected {operation}");
                throw new WatchStreamNotRunningException($"No ready stream, so there is no drive to {operation}.");
            }

            _watchOperations.Add(operation);
        }
    }

    async IAsyncEnumerable<WatchStreamItem> StreamAsync(IReadOnlyList<IndexWatchTarget> targets,
        Action reportStreamReady, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = new Stream(this, [.. targets], reportStreamReady, cancellationToken);
        lock (_stateLock)
        {
            _current = stream;
        }

        _startedStreams.Writer.TryWrite(stream);
        try
        {
            await foreach (var item in stream.Items.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_current, stream))
                {
                    _current = null;
                }
            }

            stream.MarkEnded();
        }
    }

    /// <summary>One started stream: its targets, its readiness callback, and its item queue.</summary>
    internal sealed class Stream(ReadinessScriptedWatchSource owner, IReadOnlyList<IndexWatchTarget> targets,
        Action reportStreamReady, CancellationToken cancellationToken)
    {
        readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool _ready;

        public IReadOnlyList<IndexWatchTarget> Targets { get; } = targets;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public Channel<WatchStreamItem> Items { get; } = Channel.CreateUnbounded<WatchStreamItem>();

        public bool IsReady
        {
            get
            {
                lock (owner._stateLock)
                {
                    return _ready;
                }
            }
        }

        /// <summary>Completes once the stream's iterator has left its finally.</summary>
        public Task Ended => _ended.Task.WaitAsync(FakeIndexWatchSource.HangGuard);

        /// <summary>Marks the stream live for per-drive calls, then tells the index.</summary>
        public void ReportReady()
        {
            lock (owner._stateLock)
            {
                _ready = true;
            }

            reportStreamReady();
        }

        /// <summary>Calls this stream's readiness callback without making the stream live.</summary>
        public void ReportReadyWithoutGoingLive() => reportStreamReady();

        public void Fail(Exception failure) => Items.Writer.TryComplete(failure);

        internal void MarkEnded() => _ended.TrySetResult();
    }
}
