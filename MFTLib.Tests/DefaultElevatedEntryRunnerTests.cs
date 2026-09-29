using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize] // replaces the process-wide DefaultElevatedEntryRunner._exitProcess seam
public class DefaultElevatedEntryRunnerTests
{
    [TestCleanup]
    public void Cleanup()
    {
        DefaultElevatedEntryRunner.ResetToDefaults();
        BrokerDiagnostics.ResetToDefaults();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void RunBroker_NullPipeName_ExitsWithCode1_WithoutConnecting()
    {
        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        new DefaultElevatedEntryRunner().RunBroker(null);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_ValidPipeName_ConnectsRealNamedPipe_ServesUntilControlEof_ExitsWithCode0()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");

        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // RunBroker blocks synchronously (.GetAwaiter().GetResult()) for the whole session, so
        // drive it from a background thread while this thread plays the non-elevated caller's
        // side of the real control pipe. Leaving the server's scope closes that end, which is the
        // control EOF that ends the session.
        Task runTask;
        await using (var server = new NamedPipeServerStream(
                         pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(pipeName));
            await server.WaitForConnectionAsync(cts.Token);
        }

        await runTask.WaitAsync(cts.Token);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");
        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        // The first diagnostics line parks the log writer on a gate, so lines are still queued when
        // the session fails. The clock is never advanced, so the flush's bound cannot end the wait.
        var sink = new TestGate();
        var appended = new List<string>();
        BrokerDiagnostics.Enable("broker");
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(line =>
        {
            sink.MarkEntered();
            sink.WaitForReleaseAsync(CancellationToken.None).WaitAsync(HangGuard, CancellationToken.None)
                .GetAwaiter().GetResult();
            lock (appended)
            {
                appended.Add(line);
            }
        }));
        var clock = new FlushBoundSignalingClock();

        using var cts = new CancellationTokenSource(HangGuard);
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner(clock).RunBroker(pipeName));
        await server.WaitForConnectionAsync(cts.Token);

        // A frame of no known kind ends the session with InvalidDataException.
        await server.WriteAsync(new byte[] { 1, 0, 0, 0, 200 }, cts.Token);
        await server.FlushAsync(cts.Token);
        await sink.Entered.WaitAsync(cts.Token);
        await clock.FlushBoundStarted.WaitAsync(cts.Token);

        Assert.IsFalse(runTask.IsCompleted, "The runner must wait for the queued lines while the sink is held.");
        sink.Release();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => runTask.WaitAsync(cts.Token));
        Assert.IsNull(exitCode);
        lock (appended)
        {
            Assert.IsTrue(appended.Any(line => line.Contains("frame read kind=200", StringComparison.Ordinal)),
                "The line logged as the session failed must reach the log before the runner leaves.");
        }
    }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    // A clock that is never advanced and signals when the runner starts its exit flush's bound,
    // the only timer the runner creates on it.
    sealed class FlushBoundSignalingClock : FakeTimeProvider
    {
        readonly TaskCompletionSource _flushBoundStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FlushBoundStarted => _flushBoundStarted.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _flushBoundStarted.TrySetResult();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
