using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The client's inline content read over an in-process host: the answers, the chunking, cancellation, and what one
// operation's failure leaves alone.
public partial class BrokerProcessTests
{
    sealed class AnswerLog
    {
        readonly object _gate = new();

        public List<(int Position, InlineContentOutcome Outcome, byte[] Content)> Answers { get; } = [];

        public InlineContentSink Sink => (position, outcome, content) =>
        {
            lock (_gate)
            {
                Answers.Add((position, outcome, content.ToArray()));
            }
        };
    }

    static InlineContentRequest[] RequestsFor(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new InlineContentRequest(index, index + 10L, 1))];

    [TestMethod]
    public async Task ReadInlineContent_RoundTripsReturnedNotInlineSupersededAndUnreadableAnswers()
    {
        VolumeKey? readVolume = null;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (volume, _, requests, sink, _) =>
            {
                readVolume = volume;
                foreach (var request in requests.Span)
                {
                    switch (request.RecordNumber % 4)
                    {
                        case 0:
                            sink(request.Position, InlineContentOutcome.Returned, BitConverter.GetBytes(request.RecordNumber));
                            break;
                        case 1:
                            sink(request.Position, InlineContentOutcome.NotInline, []);
                            break;
                        case 2:
                            sink(request.Position, InlineContentOutcome.Superseded, []);
                            break;
                        default:
                            sink(request.Position, InlineContentOutcome.Unreadable, []);
                            break;
                    }
                }
            }
        }));
        var log = new AnswerLog();

        await broker.Process.ReadInlineContentAsync('e', 1, RequestsFor(8), log.Sink, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(new VolumeKey('E'), readVolume);
        var byPosition = log.Answers.OrderBy(answer => answer.Position).ToArray();
        Assert.AreSequenceEqual(Enumerable.Range(0, 8), byPosition.Select(answer => answer.Position));
        for (var position = 0; position < 8; position++)
        {
            var record = position + 10L;
            var (_, outcome, content) = byPosition[position];
            Assert.AreEqual((record % 4) switch
            {
                0 => InlineContentOutcome.Returned,
                1 => InlineContentOutcome.NotInline,
                2 => InlineContentOutcome.Superseded,
                _ => InlineContentOutcome.Unreadable
            }, outcome);
            Assert.AreSequenceEqual(outcome == InlineContentOutcome.Returned ? BitConverter.GetBytes(record) : [], content);
        }
    }

    [TestMethod]
    public async Task ReadInlineContent_ReturnedEmptyContentStaysDistinctFromNotInline()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, _) =>
            {
                sink(requests.Span[0].Position, InlineContentOutcome.Returned, []);
            }
        }));
        var log = new AnswerLog();

        await broker.Process.ReadInlineContentAsync('C', 1, RequestsFor(1), log.Sink, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        var answer = Assert.ContainsSingle(log.Answers);
        Assert.AreEqual(InlineContentOutcome.Returned, answer.Outcome);
        Assert.IsEmpty(answer.Content);
    }

    [TestMethod]
    public async Task ReadInlineContent_NoRequests_OpensNoChannel()
    {
        var opens = 0;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, _, _, _) => throw new InvalidOperationException("no read should run")
        }), wrapConnector: connector => async (pipeName, cancellationToken) =>
        {
            Interlocked.Increment(ref opens);
            return await connector(pipeName, cancellationToken);
        });

        await broker.Process.ReadInlineContentAsync('C', 1, ReadOnlyMemory<InlineContentRequest>.Empty,
            new AnswerLog().Sink, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(0, opens);
    }

    [TestMethod]
    public async Task ReadInlineContent_MoreRequestsThanOneFrameHolds_RunsOneChannelPerChunkInOrder()
    {
        const int chunkLimit = BrokerProtocol.MaximumInlineContentRequestsPerFrame;
        var chunks = new List<(long First, int Length)>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, _) =>
            {
                lock (chunks)
                {
                    chunks.Add((requests.Span[0].RecordNumber, requests.Length));
                }

                foreach (var request in requests.Span)
                {
                    sink(request.Position, InlineContentOutcome.NotInline, []);
                }
            }
        }));
        var requests = RequestsFor(chunkLimit + 1);
        var answered = new bool[requests.Length];
        var duplicates = 0;

        await broker.Process.ReadInlineContentAsync('C', 1, requests, (position, _, _) =>
        {
            duplicates += answered[position] ? 1 : 0;
            answered[position] = true;
        }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreSequenceEqual([(10L, chunkLimit), (10L + chunkLimit, 1)], chunks,
            "the first frame holds the most requests the limit allows, and the last request needs a second channel");
        Assert.AreEqual(0, duplicates);
        Assert.IsTrue(answered.All(done => done), "every request is answered exactly once across both channels");
    }

    [TestMethod]
    public async Task ReadInlineContent_CancelledMidStream_EndsTheChannelAndTheHostObservesIt()
    {
        const int requestCount = 8193;
        var hostCancelled = new TestGate();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, token) =>
            {
                foreach (var request in requests.Span[..requestCount])
                {
                    sink(request.Position, InlineContentOutcome.Returned, new byte[] { 1 });
                }

                sink(requests.Span[^1].Position, InlineContentOutcome.Returned,
                    new byte[JournalBrokerHost.InlineContentBatchBytes]);
                token.WaitHandle.WaitOne(HangGuard);
                if (token.IsCancellationRequested)
                {
                    hostCancelled.MarkEntered();
                }

                token.ThrowIfCancellationRequested();
            }
        }));
        using var cancellation = new CancellationTokenSource();
        Action cancel = cancellation.Cancel;
        var delivered = 0;
        var token = cancellation.Token;

        var read = broker.Process.ReadInlineContentAsync('C', 1, RequestsFor(requestCount + 1), (_, _, _) =>
        {
            delivered++;
            cancel();
        }, token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(HangGuard, TestContext.CancellationToken));
        await hostCancelled.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(4096, delivered, "cancellation inside one buffered frame stops at the next bounded token check");
    }

    [TestMethod]
    public async Task ReadInlineContent_OneVolumesFailure_IsReportedToItsOwnCallerAndLeavesAnotherVolumesReadAlone()
    {
        var healthyEntered = new TestGate();
        var healthyMayFinish = new TestGate();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (volume, _, requests, sink, _) =>
            {
                if (volume == 'D')
                {
                    healthyEntered.Entered.Wait(HangGuard, CancellationToken.None);
                    throw new IOException("volume D could not be read");
                }

                healthyEntered.MarkEntered();
                healthyMayFinish.WaitForRelease();
                foreach (var request in requests.Span)
                {
                    sink(request.Position, InlineContentOutcome.NotInline, []);
                }
            }
        }));
        var healthyLog = new AnswerLog();
        var healthy = broker.Process.ReadInlineContentAsync('C', 1, RequestsFor(2), healthyLog.Sink, CancellationToken.None);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.ReadInlineContentAsync('D', 1, RequestsFor(1), new AnswerLog().Sink, CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual("volume D could not be read", failure.Message);
        Assert.IsFalse(healthy.IsCompleted, "C's read is still running while D has failed");
        healthyMayFinish.Release();
        await healthy.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(2, healthyLog.Answers);
        var volume = await broker.Process.QueryVolumeAsync('C', CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(Volume.MftValidDataLength, volume.MftValidDataLength, "the session still serves control requests");
    }

    [TestMethod]
    public async Task ReadInlineContent_SinkThrows_PropagatesItAndCancelsTheHostRead()
    {
        var hostCancelled = new TestGate();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, token) =>
            {
                // The second answer closes the first one's batch, so a frame reaches the client while the read continues.
                sink(requests.Span[0].Position, InlineContentOutcome.Returned, new byte[600 * 1024]);
                sink(requests.Span[1].Position, InlineContentOutcome.Returned, new byte[600 * 1024]);
                token.WaitHandle.WaitOne(HangGuard);
                if (token.IsCancellationRequested)
                {
                    hostCancelled.MarkEntered();
                }
            }
        }));

        var exception = await Assert.ThrowsExactlyAsync<InvalidCastException>(() =>
            broker.Process.ReadInlineContentAsync('C', 1, RequestsFor(2),
                    (_, _, _) => throw new InvalidCastException("handler failed"), CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual("handler failed", exception.Message);
        await hostCancelled.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ReadInlineContent_HostAnswersAPositionNobodyAskedFor_IsALostChannel()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, _) =>
            {
                sink(requests.Span[0].Position, InlineContentOutcome.NotInline, []);
                sink(1, InlineContentOutcome.NotInline, []);
            }
        }));

        // Positions 0 and 2 are requested, so position 1 is inside the range but was never asked for.
        InlineContentRequest[] requested = [new(0, 10L, 1), new(2, 12L, 1)];
        var exception = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            broker.Process.ReadInlineContentAsync('C', 1, requested, new AnswerLog().Sink, CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("answered position 1 that", exception.Message);
    }

    [TestMethod]
    public async Task ReadInlineContent_HostAnswersAPositionTwice_IsALostChannel()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, _) =>
            {
                sink(requests.Span[0].Position, InlineContentOutcome.NotInline, []);
                sink(requests.Span[0].Position, InlineContentOutcome.NotInline, []);
            }
        }));

        await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            broker.Process.ReadInlineContentAsync('C', 1, RequestsFor(2), new AnswerLog().Sink, CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ReadInlineContent_HostCompletesWithARequestUnanswered_IsALostChannel()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, _) =>
                sink(requests.Span[0].Position, InlineContentOutcome.NotInline, [])
        }));

        var exception = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            broker.Process.ReadInlineContentAsync('C', 1, RequestsFor(2), new AnswerLog().Sink, CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("unanswered", exception.Message);
    }
}
