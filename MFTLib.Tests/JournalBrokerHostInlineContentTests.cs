using System.Buffers;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The host's side of an inline content channel, spoken to in raw frames: the batches, the completion, the errors, the
// cancellation a closed pipe causes, and the liveness a frame write counts as.
[TestClass]
[DoNotParallelize]
public sealed class JournalBrokerHostInlineContentTests
{
    static readonly byte[] SmallContent = [1, 2, 3];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Request_IsReadOnTheChannelsVolumeAndEveryAnswerArrivesBeforeTheCompletion()
    {
        VolumeKey? readVolume = null;
        InlineContentRequest[]? readRequests = null;
        var host = BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (volume, _, requests, sink, _) =>
            {
                readVolume = volume;
                readRequests = requests.ToArray();
                sink(10, InlineContentOutcome.Returned, SmallContent);
                sink(11, InlineContentOutcome.NotInline, []);
                sink(12, InlineContentOutcome.Unreadable, []);
            }
        });
        await using var harness = new HostChannelHarness(host);
        InlineContentRequest[] sent = [new(10, 5, 1), new(11, 6, 2), new(12, 7, 3)];

        var pipe = await harness.OpenInlineContentChannelAsync('D', sent);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(new VolumeKey('D'), readVolume);
        Assert.AreSequenceEqual(sent, readRequests!);
        Assert.AreSequenceEqual([BrokerFrameKind.InlineContentBatch, BrokerFrameKind.InlineContentCompleted],
            [.. frames.Select(frame => frame.Kind)]);
        Assert.AreSequenceEqual(
            [
                new BrokerInlineContentAnswer(10, InlineContentOutcome.Returned, 0, 3),
                new BrokerInlineContentAnswer(11, InlineContentOutcome.NotInline, 3, 0),
                new BrokerInlineContentAnswer(12, InlineContentOutcome.Unreadable, 3, 0)
            ], frames[0].InlineContentAnswers);
        Assert.AreSequenceEqual(SmallContent, frames[0].InlineContent.ToArray());
    }

    [TestMethod]
    public async Task VolumeSerialMismatch_FromTheVolumeAccess_RefusesTheReadBeforeAnyAnswer()
    {
        ulong? readSerial = null;
        var host = BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, serial, _, _, _) =>
            {
                readSerial = serial;
                throw new IOException("volume serial mismatch");
            }
        });
        await using var harness = new HostChannelHarness(host);
        var pipe = await harness.OpenChannelAsync('C');
        await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteReadInlineContent(writer,
            BrokerFrame.ReadInlineContent(new[] { new InlineContentRequest(0, 5, 1) }, 0xA1B2C3D4)));

        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(0xA1B2C3D4u, readSerial);
        var error = Assert.ContainsSingle(frames);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("volume serial mismatch", error.Message);
    }

    [TestMethod]
    public async Task Answers_PastTheBatchSizeSplitIntoBatchesThatEachStayUnderIt()
    {
        const int answerCount = 5;
        var content = new byte[400 * 1024];
        var host = BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, _) =>
            {
                foreach (var request in requests.Span)
                {
                    sink(request.Position, InlineContentOutcome.Returned, content);
                }
            }
        });
        await using var harness = new HostChannelHarness(host);
        var requests = Enumerable.Range(0, answerCount).Select(index => new InlineContentRequest(index, index, 1)).ToArray();

        var pipe = await harness.OpenInlineContentChannelAsync('C', requests);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        var batches = frames.Where(frame => frame.Kind == BrokerFrameKind.InlineContentBatch).ToArray();
        Assert.AreSequenceEqual([2, 2, 1], [.. batches.Select(batch => batch.InlineContentAnswers.Length)]);
        Assert.IsTrue(batches.All(batch => batch.InlineContent.Length <= JournalBrokerHost.InlineContentBatchBytes));
        Assert.AreSequenceEqual(Enumerable.Range(0, answerCount),
            batches.SelectMany(batch => batch.InlineContentAnswers).Select(answer => answer.Position).Order());
        Assert.AreEqual(BrokerFrameKind.InlineContentCompleted, frames[^1].Kind);
    }

    [TestMethod]
    public async Task Answers_WhosePerAnswerMetadataAloneCrossesTheBatchSize_SplitByEncodedAnswerSize()
    {
        const int answerCount = 120_000;
        var host = BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, requests, sink, _) =>
            {
                foreach (var request in requests.Span)
                {
                    sink(request.Position, InlineContentOutcome.NotInline, []);
                }
            }
        });
        await using var harness = new HostChannelHarness(host);
        var requests = Enumerable.Range(0, answerCount).Select(index => new InlineContentRequest(index, index, 1)).ToArray();

        var pipe = await harness.OpenInlineContentChannelAsync('C', requests);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        var batches = frames.Where(frame => frame.Kind == BrokerFrameKind.InlineContentBatch).ToArray();
        var encodedSizes = batches.Select(batch => batch.InlineContentAnswers
            .Sum(answer => BrokerProtocol.InlineContentAnswerBytes(answer.Length))).ToArray();
        var perBatchAnswers = JournalBrokerHost.InlineContentBatchBytes / BrokerProtocol.InlineContentAnswerBytes(0);
        Assert.AreSequenceEqual([perBatchAnswers, answerCount - perBatchAnswers],
            batches.Select(batch => batch.InlineContentAnswers.Length), "no content at all, yet two batches");
        Assert.IsTrue(encodedSizes.All(size => size <= JournalBrokerHost.InlineContentBatchBytes),
            "every batch's encoded answers fit the batch size");
        Assert.IsTrue(batches.All(batch => batch.InlineContent.IsEmpty), "the size came from metadata, not content");
        Assert.AreEqual(BrokerFrameKind.InlineContentCompleted, frames[^1].Kind);
    }

    [TestMethod]
    public async Task ReadFailure_WritesAnErrorWithTheMessageAndNoCompletion()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, _, sink, _) =>
            {
                sink(0, InlineContentOutcome.Returned, SmallContent);
                throw new IOException("volume vanished");
            }
        });
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenInlineContentChannelAsync('C', [new InlineContentRequest(0, 5, 1)]);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        var error = Assert.ContainsSingle(frames);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("volume vanished", error.Message);
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(90, 'C')));
        Assert.AreEqual(90u, (await harness.ReadControlAsync()).RequestId, "The session keeps serving after one channel's failure.");
    }

    [TestMethod]
    public async Task RequestOutOfAscendingOrder_WritesAMalformedRequestErrorAndNeverReads()
    {
        var reads = 0;
        var host = BrokerHostFactory.Create(new() { ReadInlineContent = (_, _, _, _, _) => reads++ });
        await using var harness = new HostChannelHarness(host);
        var pipe = await harness.OpenChannelAsync('C');
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteReadInlineContent(buffer, BrokerFrame.ReadInlineContent(new[] { new InlineContentRequest(0, 9, 1), new InlineContentRequest(1, 8, 1) }, 0xA1B2C3D4));
        var ascending = buffer.WrittenSpan.ToArray();

        await HostChannelHarness.WriteFrameAsync(pipe, writer => writer.Write(ascending));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        var error = Assert.ContainsSingle(frames);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.Contains("malformed", error.Message!);
        Assert.AreEqual(0, reads);
    }

    [TestMethod]
    public async Task ClientClosingItsPipe_CancelsTheReadWithoutAnyFrame()
    {
        var entered = new TestGate();
        var cancelled = new TestGate();
        var host = BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (_, _, _, _, token) =>
            {
                entered.MarkEntered();
                token.WaitHandle.WaitOne(HangGuard);
                if (token.IsCancellationRequested)
                {
                    cancelled.MarkEntered();
                }

                token.ThrowIfCancellationRequested();
            }
        });
        await using var harness = new HostChannelHarness(host);
        var pipe = await harness.OpenInlineContentChannelAsync('C', [new InlineContentRequest(0, 5, 1)]);
        await entered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await pipe.DisposeAsync();

        await cancelled.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
    }
}
