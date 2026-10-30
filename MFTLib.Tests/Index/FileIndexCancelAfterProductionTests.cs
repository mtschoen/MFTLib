using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Mirrors git-wizard's CancellationAfterRescanProduction_PreservesSnapshotAndWatch: the caller's
///     token is cancelled from inside the producer, after the block is written and before the index
///     publishes it. The cancelled rescan must leave the old snapshot and the running watch in place.
/// </summary>
[TestClass]
public sealed class FileIndexCancelAfterProductionTests
{
    const uint VolumeSerial = 0x5EED;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1)]
    [DataRow(100)]
    public async Task Rescan_CallerCancelsInsideProducer_PreservesSnapshot(int iterations)
    {
        using var fixture = new IndexTreeFixture(seedDocuments: false);
        var source = new ScriptedWatchSource { CatchUpOnStart = true };
        var cancelOnProduce = new Queue<Action>();
        var productions = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            productions++;
            var produced = SeededBlocks.Produce(request, 7, 4096, SeededBlocks.SeededMoment);
            if (cancelOnProduce.TryDequeue(out var cancel))
            {
                cancel();
            }

            return produced;
        }

        var options = fixture.Options(volumeSerial: VolumeSerial, producer: ProducerSelection.MftOnly) with
        {
            MftSource = new MftIndexSource(Produce, source.IndexSource)
        };
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        await index.StartWatchingAsync(TestContext.CancellationToken);
        var committed = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            var original = index.Root(TestContext.CancellationToken).RootBlock.Block;
            cancelOnProduce.Enqueue(cancellation.Cancel);
            try
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                    () => index.RescanAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30)));
            }
            finally
            {
                cancelOnProduce.Clear();
            }

            if (!ReferenceEquals(original, index.Root(TestContext.CancellationToken).RootBlock.Block))
            {
                committed++;
            }

            await index.RescanAsync(TestContext.CancellationToken);
            await index.StartWatchingAsync(TestContext.CancellationToken);
        }

        Assert.AreEqual(0, committed, "cancelled rescans that replaced the snapshot (productions " + productions + ")");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rescan_CallerCancelsBehindABlockedCallback_PreservesSnapshot(bool cancelAsynchronously)
    {
        using var fixture = new IndexTreeFixture(seedDocuments: false);
        var source = new ScriptedWatchSource { CatchUpOnStart = true };
        var blocked = new BlockedCancellation(cancelAsynchronously);
        try
        {
            var options = fixture.Options(volumeSerial: VolumeSerial, producer: ProducerSelection.MftOnly) with
            {
                MftSource = new MftIndexSource(blocked.ProduceAsync, source.IndexSource)
            };
            await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
            await index.StartWatchingAsync(TestContext.CancellationToken);
            var original = index.Root(TestContext.CancellationToken).RootBlock.Block;
            blocked.Arm();
            try
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                    () => index.RescanAsync(blocked.Token).WaitAsync(HangGuard));
            }
            finally
            {
                await blocked.ReleaseAsync();
            }

            Assert.AreSame(original, index.Root(TestContext.CancellationToken).RootBlock.Block,
                "a rescan whose every caller cancelled never publishes");
            Assert.HasCount(1, source.Starts);
            Assert.AreEqual(0, source.Watches.Single().DisposeCount);
        }
        finally
        {
            blocked.Dispose();
        }
    }

    /// <summary>
    ///     A caller token whose cancellation is held inside a blocking callback, so the library's own
    ///     registration on it has not run when the producer returns. A blocking callback is registered
    ///     both before and after the rescan's registration, so either execution order reaches one first.
    /// </summary>
    sealed class BlockedCancellation(bool cancelAsynchronously) : IDisposable
    {
        readonly CancellationTokenSource _source = new();
        readonly TestGate _callback = new();
        readonly List<CancellationTokenRegistration> _registrations = [];
        Task _cancelling = Task.CompletedTask;
        bool _armed;

        internal CancellationToken Token => _source.Token;

        internal void Arm()
        {
            _registrations.Add(_source.Token.Register(Block));
            _armed = true;
        }

        internal async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            var produced = await SeededBlocks.Produce(request, 7, 4096, SeededBlocks.SeededMoment);
            if (_armed)
            {
                _registrations.Add(_source.Token.Register(Block));
                _cancelling = cancelAsynchronously ? _source.CancelAsync() : Task.Run(_source.Cancel, CancellationToken.None);
                await _callback.Entered.WaitAsync(HangGuard, CancellationToken.None);
            }

            return produced;
        }

        internal Task ReleaseAsync()
        {
            _callback.Release();
            return _cancelling.WaitAsync(HangGuard);
        }

        void Block()
        {
            _callback.MarkEntered();
            _callback.WaitForRelease();
        }

        public void Dispose()
        {
            _callback.Release();
            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }
            _source.Dispose();
        }
    }
}
