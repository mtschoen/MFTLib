using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.Index.InlineContentIndexes;

namespace MFTLib.Tests.Index;

/// <summary>
///     A broker-backed source reads inline content through its broker instead of answering
///     <see cref="RootOperationOutcome.NotApplicable" />.
/// </summary>
[TestClass]
public sealed class FileIndexBrokerInlineContentTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task IndexSourceOfABrokerProducer_CarriesABrokerInlineContentReader()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create());
        var process = broker.Process;

        var source = new BrokerMftBlockProducer(_ => Task.FromResult(process)).CreateIndexSource();

        Assert.IsInstanceOfType<BrokerInlineContentReader>(source.InlineContentReader);
    }

    [TestMethod]
    [DataRow(0UL)]
    [DataRow(0xA1B2C3D4UL)]
    [DataRow(0x1111111122222222UL)]
    public async Task Read_ThroughABrokerReader_ReturnsTheHostsBytesAndSendsOnlyTheLiveFilesRecords(ulong volumeSerial)
    {
        InlineContentRequest[]? hostRequests = null;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (volume, serial, requests, sink, _) =>
            {
                Assert.AreEqual(new VolumeKey(DriveLetter), volume);
                Assert.AreEqual(volumeSerial, serial);
                hostRequests = requests.ToArray();
                foreach (var request in requests.Span)
                {
                    sink(request.Position, InlineContentOutcome.Returned, BitConverter.GetBytes(request.RecordNumber));
                }
            }
        }));
        var process = broker.Process;
        using var directory = new TempDirectory();
        var options = new FileIndexOptions
        {
            CacheMode = CacheMode.Disabled,
            MftSource = new MftIndexSource(Produce, inlineContentReader: new BrokerInlineContentReader(_ => Task.FromResult(process)))
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey(DriveLetter), directory.Path, volumeSerial));
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        var entries = new[] { Entry(index, 8), Entry(index, 6), Entry(index, 10), Entry(index, 7) };
        var results = new List<(int Index, InlineContentOutcome Outcome, byte[] Content)>();

        var result = await index.ReadInlineContentAsync(entries,
            answer => results.Add((answer.Index, answer.Outcome, answer.Content.ToArray())), CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.AreSequenceEqual([new InlineContentRequest(3, 7, 3), new InlineContentRequest(0, 8, 4)], hostRequests!);
        var byPosition = results.OrderBy(answer => answer.Index).ToArray();
        Assert.AreSequenceEqual([0, 1, 2, 3], byPosition.Select(answer => answer.Index));
        Assert.AreSequenceEqual(BitConverter.GetBytes(8L), byPosition[0].Content);
        Assert.AreEqual(InlineContentOutcome.NotInline, byPosition[1].Outcome, "a directory is decided without the broker");
        Assert.AreEqual(InlineContentOutcome.Superseded, byPosition[2].Outcome, "a deleted file is decided without the broker");
        Assert.AreSequenceEqual(BitConverter.GetBytes(7L), byPosition[3].Content);
    }

    [TestMethod]
    public async Task Read_WhenTheBrokerFails_ReportsFailedWithTheHostsMessage()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, _, _, _) => throw new IOException("the volume went away")
        }));
        var process = broker.Process;
        await using var index = await OpenAsync(new BrokerInlineContentReader(_ => Task.FromResult(process)));

        var result = await index.ReadInlineContentAsync([Entry(index, 7)], _ => { }, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Failed, result.Outcome);
        Assert.AreEqual("the volume went away", result.Failure!.Message);
    }

    // Each chunk of a broker read opens and checks the volume on its own channel, and a chunk's answers stream to the
    // handler before the next chunk opens: a refusal of the second chunk fails the call after the first chunk's answers.
    [TestMethod]
    public async Task Read_WhenTheBrokerRefusesTheSecondChunk_FailsAfterDeliveringExactlyTheFirstChunk()
    {
        const int chunkLimit = BrokerProtocol.MaximumInlineContentRequestsPerFrame;
        var hostReads = 0;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (volume, serial, requests, sink, token) =>
            {
                if (Interlocked.Increment(ref hostReads) == 2)
                {
                    throw new IOException("the volume serial no longer matches");
                }

                AnswerEveryRequest(volume, serial, requests, sink, token);
            }
        }));
        var process = broker.Process;
        await using var index = await OpenAsync(new BrokerInlineContentReader(_ => Task.FromResult(process)));
        var entries = Enumerable.Repeat(Entry(index, 7), chunkLimit + 1).ToArray();
        var answered = new bool[entries.Length];
        var answers = 0;

        var result = await index.ReadInlineContentAsync(entries, answer =>
            {
                answers++;
                answered[answer.Index] = true;
            }, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Failed, result.Outcome);
        Assert.AreEqual("the volume serial no longer matches", result.Failure!.Message);
        Assert.AreEqual(2, hostReads, "the second chunk opened its own channel and was refused there");
        Assert.AreEqual(chunkLimit, answers, "exactly the first chunk's answers reached the handler");
        Assert.AreEqual(chunkLimit, answered.Count(done => done), "each of them once");
    }

    [TestMethod]
    public async Task Read_ThroughABrokerReader_HandlerThatCancelsAndThrows_PropagatesItsOwnException()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new() { ReadInlineContent = AnswerEveryRequest }));
        var process = broker.Process;
        await using var index = await OpenAsync(new BrokerInlineContentReader(_ => Task.FromResult(process)));
        using var cancellation = new CancellationTokenSource();
        var thrown = new InvalidOperationException("handler failed after cancelling");

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => index.ReadInlineContentAsync([Entry(index, 7), Entry(index, 8)], _ =>
            {
                // ReSharper disable once AccessToDisposedClosure
                cancellation.Cancel();
                throw thrown;
            }, cancellation.Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreSame(thrown, failure, "the handler's exception wins over the cancellation that closed the channel");
    }

    [TestMethod]
    public async Task Read_ThroughABrokerReader_CancelledByTheLastAnswer_ThrowsInsteadOfSucceeding()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new() { ReadInlineContent = AnswerEveryRequest }));
        var process = broker.Process;
        await using var index = await OpenAsync(new BrokerInlineContentReader(_ => Task.FromResult(process)));
        using var cancellation = new CancellationTokenSource();
        var answers = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => index.ReadInlineContentAsync([Entry(index, 7)], _ =>
            {
                answers++;
                // ReSharper disable once AccessToDisposedClosure
                cancellation.Cancel();
            }, cancellation.Token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(1, answers, "the one answer was delivered before the cancellation ended the call");
    }

    static void AnswerEveryRequest(VolumeKey volume, ulong volumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken)
    {
        foreach (var request in requests.Span)
        {
            sink(request.Position, InlineContentOutcome.Returned, BitConverter.GetBytes(request.RecordNumber));
        }
    }
}
