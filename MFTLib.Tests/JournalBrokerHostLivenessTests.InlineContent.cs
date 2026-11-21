using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// An inline content read is liveness-checked like any operation: each batch it flushes restarts the processing clock.
public partial class JournalBrokerHostLivenessTests
{
    static readonly byte[] InlineSmallContent = [1, 2, 3];

    [TestMethod]
    public async Task BatchFlushes_RestartTheProcessingClock_SoAReadFlushingEveryTwentyFiveSecondsIsNeverStalled()
    {
        var liveness = new Liveness();
        var afterFirst = new TestGate();
        var afterSecond = new TestGate();
        var content = new byte[600 * 1024];
        await using var harness = new HostChannelHarness(liveness.Host(readInlineContent: (_, _, _, sink, _) =>
        {
            // Each 600 KiB answer closes the batch before it, so each sink call after the first writes a frame.
            sink(0, InlineContentOutcome.Returned, content);
            afterFirst.MarkEntered();
            afterFirst.WaitForRelease();
            sink(1, InlineContentOutcome.Returned, content);
            afterSecond.MarkEntered();
            afterSecond.WaitForRelease();
            sink(2, InlineContentOutcome.Returned, content);
        }));
        var pipe = await harness.OpenInlineContentChannelAsync('C', [new(0, 1, 1), new(1, 2, 1), new(2, 3, 1)]);
        await afterFirst.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await AdvanceAsync(liveness, 5);
        afterFirst.Release();
        var first = await HostChannelHarness.ReadFrameAsync(pipe);
        Assert.AreEqual(BrokerFrameKind.InlineContentBatch, first!.Value.Kind);
        await afterSecond.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await AdvanceAsync(liveness, 5);
        afterSecond.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Stalled),
            "Fifty seconds passed in two quiet halves, each ended by a batch flush that restarted the clock. The flush publishes progress and queues the frame write together, so this test does not separate the two.");
        Assert.AreSequenceEqual(
            [BrokerFrameKind.InlineContentBatch, BrokerFrameKind.InlineContentBatch, BrokerFrameKind.InlineContentCompleted],
            [.. frames.Where(frame => frame.Kind != BrokerFrameKind.Heartbeat).Select(frame => frame.Kind)]);
    }

    [TestMethod]
    public async Task SmallAnswers_CountAsProgressBeforeTheirBatchFills()
    {
        var liveness = new Liveness();
        var afterFirst = new TestGate();
        var afterSecond = new TestGate();
        var afterThird = new TestGate();
        await using var harness = new HostChannelHarness(liveness.Host(readInlineContent: (_, _, _, sink, _) =>
        {
            sink(0, InlineContentOutcome.Returned, [1]);
            afterFirst.MarkEntered();
            afterFirst.WaitForRelease();
            sink(1, InlineContentOutcome.Returned, [2]);
            afterSecond.MarkEntered();
            afterSecond.WaitForRelease();
            sink(2, InlineContentOutcome.Returned, [3]);
            afterThird.MarkEntered();
            afterThird.WaitForRelease();
        }));
        var pipe = await harness.OpenInlineContentChannelAsync('C', [new(0, 1, 1), new(1, 2, 1), new(2, 3, 1)]);
        await afterFirst.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await AdvanceAsync(liveness, 2);
        afterFirst.Release();
        await afterSecond.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await AdvanceAsync(liveness, 2);
        afterSecond.Release();
        await afterThird.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await AdvanceAsync(liveness, 3);
        afterThird.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreSequenceEqual([BrokerFrameKind.InlineContentBatch, BrokerFrameKind.InlineContentCompleted],
            frames.Select(frame => frame.Kind));
        Assert.AreSequenceEqual(
            [new BrokerInlineContentAnswer(0, InlineContentOutcome.Returned, 0, 1),
             new BrokerInlineContentAnswer(1, InlineContentOutcome.Returned, 1, 1),
             new BrokerInlineContentAnswer(2, InlineContentOutcome.Returned, 2, 1)], frames[0].InlineContentAnswers);
        Assert.AreSequenceEqual(InlineSmallContent, frames[0].InlineContent.ToArray());
    }

    [TestMethod]
    public async Task ReadThatMakesNoProgressForTheProcessingLimit_IsStalledAndWritesNothingMore()
    {
        var liveness = new Liveness();
        var entered = new TestGate();
        var release = new TestGate();
        await using var harness = new HostChannelHarness(liveness.Host(readInlineContent: (_, _, _, sink, _) =>
        {
            entered.MarkEntered();
            release.WaitForRelease();
            sink(0, InlineContentOutcome.Returned, InlineSmallContent);
        }));
        var pipe = await harness.OpenInlineContentChannelAsync('C', [new InlineContentRequest(0, 1, 1)]);
        await entered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await AdvanceAsync(liveness, 9);
        release.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.Stalled, frames[^1].Kind);
        Assert.IsFalse(frames.Any(frame => frame.Kind is BrokerFrameKind.InlineContentBatch or BrokerFrameKind.InlineContentCompleted));
    }

    static async Task AdvanceAsync(Liveness liveness, int intervals)
    {
        for (var interval = 0; interval < intervals; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
        }
    }

    // The native reader's group reads are synchronous. Each one here takes 20 fake seconds, charged when the native call
    // that made it returns, so a call that made two reads before returning would pass the 30 second limit with a group
    // already complete and nothing published. The shipped reader runs unmodified: a sparse dump whose groups lie 128 KiB
    // apart, and a fragmented image whose adjacent records 31 and 32 are two groups because they sit in different runs.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GroupReadsThatTakeTwentySecondsEach_AreNotStalledBecauseEveryCompletedGroupPublishesItsAnswers(
        bool fragmented)
    {
        var liveness = new Liveness();
        using var directory = new TempDirectory("mftlib-inline-liveness");
        var numbers = fragmented ? new[] { 30, 31, 32, 33 } : [100, 228, 356];
        var contents = numbers.Select((_, index) => new[] { (byte)(0x41 + index), (byte)index }).ToArray();
        var path = WriteSlowGroupInput(directory.Path, fragmented, numbers, contents);
        var groupReads = new List<ulong>();
        var native = MFTLibNative.Hooks._readMftInlineContent;
        try
        {
            MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
            {
                native(reader, ref call, out result);
                groupReads.Add(result.ReadCalls);
                for (var interval = 0; interval < 4 * (int)result.ReadCalls; interval++)
                {
                    liveness.AdvanceOneIntervalAsync().Wait(HangGuard);
                }
            };
            await using var harness = new HostChannelHarness(liveness.Host(readInlineContent: (_, _, requests, sink, token) =>
                ReadSlowGroupInput(path, fragmented, requests, sink, token)));
            var pipe = await harness.OpenInlineContentChannelAsync('C',
                [.. numbers.Select((number, position) => new InlineContentRequest(position, number, 1))]);

            var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);

            Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Stalled),
                "Every group read took twenty seconds, and each one published its answers before the next began.");
            Assert.AreSequenceEqual(fragmented ? new ulong[] { 1, 1 } : [1, 1, 1], groupReads,
                "each native call returns after its one group read");
            Assert.AreEqual(BrokerFrameKind.InlineContentCompleted, frames[^1].Kind);
            var answers = frames.Where(frame => frame.Kind == BrokerFrameKind.InlineContentBatch)
                .SelectMany(frame => frame.InlineContentAnswers.Select(answer =>
                    (answer.Position, answer.Outcome, Content: frame.InlineContent.Slice(answer.Offset, answer.Length).ToArray())))
                .ToArray();
            Assert.AreSequenceEqual(Enumerable.Range(0, numbers.Length), answers.Select(answer => answer.Position));
            Assert.IsTrue(answers.All(answer => answer.Outcome == InlineContentOutcome.Returned));
            for (var index = 0; index < numbers.Length; index++)
            {
                CollectionAssert.AreEqual(contents[index], answers[index].Content, $"record {numbers[index]}");
            }
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
            MFTLibNative.Hooks.ResetToDefaults();
        }
    }

    // The negative control on the same shipped reader: one group read that takes 40 seconds is a genuine stall.
    [TestMethod]
    public async Task OneGroupReadThatTakesFortySeconds_IsStalledAndWritesNothingMore()
    {
        var liveness = new Liveness();
        using var directory = new TempDirectory("mftlib-inline-liveness");
        var path = WriteSlowGroupInput(directory.Path, fragmented: false, [100], [InlineSmallContent]);
        var native = MFTLibNative.Hooks._readMftInlineContent;
        try
        {
            MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
            {
                native(reader, ref call, out result);
                for (var interval = 0; interval < 8 * (int)result.ReadCalls; interval++)
                {
                    liveness.AdvanceOneIntervalAsync().Wait(HangGuard);
                }
            };
            await using var harness = new HostChannelHarness(liveness.Host(readInlineContent: (_, _, requests, sink, token) =>
                ReadSlowGroupInput(path, fragmented: false, requests, sink, token)));
            var pipe = await harness.OpenInlineContentChannelAsync('C', [new InlineContentRequest(0, 100, 1)]);

            var frames = await HostChannelHarness.ReadToEndAsync(pipe);

            Assert.AreEqual(BrokerFrameKind.Stalled, frames[^1].Kind);
            Assert.IsFalse(frames.Any(frame => frame.Kind is BrokerFrameKind.InlineContentBatch or BrokerFrameKind.InlineContentCompleted));
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
            MFTLibNative.Hooks.ResetToDefaults();
        }
    }

    static string WriteSlowGroupInput(string directory, bool fragmented, int[] numbers, byte[][] contents)
    {
        var records = new Dictionary<int, DumpRecord>
        {
            [0] = new DumpRecord("$MFT", 5),
            [5] = new DumpRecord(".", 5) { IsDirectory = true }
        };
        for (var index = 0; index < numbers.Length; index++)
        {
            records[numbers[index]] = new DumpRecord($"{index}.bin", 5) { Content = contents[index] };
        }

        if (!fragmented)
        {
            return MftDumpFixture.WriteFile(directory, MftDumpFixture.Build(1024, 384, records));
        }

        var path = Path.Combine(directory, "fragmented.img");
        FragmentedNtfsImage.Write(path, MftDumpFixture.Build(1024, 64, records), firstRunRecords: 32, holeClusters: 3);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        return path;
    }

    static void ReadSlowGroupInput(string path, bool fragmented, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken)
    {
        if (fragmented)
        {
            using var image = File.OpenHandle(path);
            using var imageReader = NativeInlineContentReader.Open(image);
            imageReader.Read(requests.Span, sink, cancellationToken);
            return;
        }

        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();
        reader.Read(requests.Span, sink, cancellationToken);
    }
}
