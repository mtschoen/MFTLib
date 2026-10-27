using System.Globalization;
using System.Text.RegularExpressions;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostLivenessTests
{
    [TestMethod]
    public async Task StalledPipe_OperationIgnoresCancellation_WritesNothingMoreAfterStalled()
    {
        var liveness = new Liveness();
        var entered = new TestGate();
        var release = new TestGate();
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(liveness.Host(scanVolume: (_, _, reporting, _, _, _) =>
        {
            reporting.Operation!.Processing("wedged step");
            entered.MarkEntered();
            release.WaitForRelease();
            return [];
        }), sectionWriter);
        var pipe = await harness.OpenScanChannelAsync('C');
        await entered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        // Six 5 second intervals reach the 30 second limit and Stalled is written; the operation ignores the
        // cancellation that follows, so the pipe stays visited for the intervals after it.
        for (var interval = 0; interval < 9; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
        }

        // The operation now finishes and tries to write its next frame to the stalled pipe.
        release.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);

        var kinds = frames.Where(frame => frame.Kind != BrokerFrameKind.Heartbeat).Select(frame => frame.Kind).ToArray();
        Assert.AreSequenceEqual([BrokerFrameKind.Cursor, BrokerFrameKind.Stalled], kinds, "Stalled is the last frame the pipe carries; the finished operation's frames are not written.");
        Assert.AreEqual(BrokerFrameKind.Stalled, frames[^1].Kind);
    }

    [TestMethod]
    public async Task StalledPipe_FrameQueuedBehindTheStalledWrite_IsNotWritten()
    {
        var liveness = new Liveness();
        var entered = new TestGate();
        var release = new TestGate();
        var stalledWrite = new HeldStalledWrite();
        var laterFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var diagnosticsDirectory = new TempDirectory("broker-diagnostics");
        BrokerDiagnostics.Enable("broker", diagnosticsDirectory.Path);
        BrokerDiagnostics.Dependencies.InstallWriter(new BrokerDiagnosticsWriter(line =>
        {
            // A frame written to volume C's pipe after the Stalled write began, other than the
            // liveness frames: the operation's own, which is about to wait for the pipe's write lock.
            var match = FrameWrite.Match(line);
            if (match.Success && stalledWrite.Started.IsCompleted &&
                int.Parse(match.Groups["kind"].Value, CultureInfo.InvariantCulture) is var kind &&
                kind != (int)BrokerFrameKind.Heartbeat && kind != (int)BrokerFrameKind.Stalled)
            {
                laterFrame.TrySetResult();
            }
        }));
        try
        {
            using var sectionWriter = new RecordingBlockSectionWriter();
            await using var harness = new HostChannelHarness(liveness.Host(scanVolume: (_, _, reporting, _, _, _) =>
            {
                reporting.Operation!.Processing("wedged step");
                entered.MarkEntered();
                release.WaitForRelease();
                return [];
            }), sectionWriter, wrapVolumePipe: (_, hostEnd) => stalledWrite.Wrap(hostEnd));
            try
            {
                var pipe = await harness.OpenScanChannelAsync('C');
                await entered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                // The Stalled write starts on the sixth interval (30 second limit, six 5 second visits);
                // the bound only makes a missing write fail the test instead of looping.
                for (var interval = 0; !stalledWrite.Started.IsCompleted; interval++)
                {
                    Assert.IsLessThan(MaximumIntervalsBeforeStalled, interval,
                        $"No Stalled write started within {MaximumIntervalsBeforeStalled} heartbeat intervals.");
                    await liveness.AdvanceOneIntervalAsync();
                }

                // The Stalled write is in flight and the pipe is marked stalled. The operation finishes
                // and queues its next frame behind that write; once the write lets go, the frame finds
                // the pipe stalled and is not written.
                release.Release();
                await laterFrame.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
                stalledWrite.Finish();
                var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);

                var kinds = frames.Where(frame => frame.Kind != BrokerFrameKind.Heartbeat).Select(frame => frame.Kind).ToArray();
                Assert.AreSequenceEqual([BrokerFrameKind.Cursor, BrokerFrameKind.Stalled], kinds);
            }
            finally
            {
                release.Release();
                stalledWrite.Finish();
            }
        }
        finally
        {
            try
            {
                await BrokerDiagnosticsTestSupport.DrainAsync();
            }
            finally
            {
                BrokerDiagnostics.Dependencies.ResetToDefaults();
            }
        }
    }

    const int MaximumIntervalsBeforeStalled = 20;

    static readonly Regex FrameWrite = MyRegex();

    // Lets every write through except one whose frame is Stalled: that write reports it started and
    // then waits until Finish, the way a write to a pipe whose reader is slow does.
    sealed class HeldStalledWrite
    {
        readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Finish() => _finish.TrySetResult();

        public Stream Wrap(Stream inner) => new HoldingStream(inner, this);

        sealed class HoldingStream(Stream inner, HeldStalledWrite owner) : DelegatingStream(inner)
        {
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (buffer.Span[4] == (byte)BrokerFrameKind.Stalled)
                {
                    owner._started.TrySetResult();
                    await owner._finish.Task.WaitAsync(HangGuard, CancellationToken.None);
                }

                await base.WriteAsync(buffer, cancellationToken);
            }
        }
    }

    [GeneratedRegex(@":mftlib-broker-43000000000000000000000000000001\]  frame write kind=(?<kind>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
