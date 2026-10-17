using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize] // replaces the FileUtilities delegate seams and the native test hooks
public class DefaultElevatedEntryRunnerTests
{
    Func<Task, Task> _waitForRunnerExit = left => left.WaitAsync(HangGuard);

    [TestCleanup]
    public async Task Cleanup()
    {
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        BrokerDiagnostics.Dependencies.ResetToDefaults();
        NativeTestHooks.NativeResetTestState();
        FileUtilities.Hooks.ResetToDefaults();
        JournalQueryFake.Reset();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void RunBroker_NullPipeName_ExitsWithCode1_WithoutConnecting()
    {
        var firstExits = new List<int>();
        var secondExits = new List<int>();
        var first = new DefaultElevatedEntryRunner(TimeProvider.System, firstExits.Add);
        var second = new DefaultElevatedEntryRunner(TimeProvider.System, secondExits.Add);
        first.RunBroker(null);
        second.RunBroker(null);
        first.RunBroker(null);
        CollectionAssert.AreEqual(new[] { 1, 1 }, firstExits);
        CollectionAssert.AreEqual(new[] { 1 }, secondExits);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_ValidControlPipe_ServesUntilControlCloses_ExitsWithCode0()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");

        int? exitCode = null;

        using var cts = new CancellationTokenSource(HangGuard);

        // RunBroker blocks synchronously (.GetAwaiter().GetResult()) for the whole session, so
        // drive it from a background thread while this thread plays the non-elevated caller's
        // side of the real control pipe. Closing that end is the control EOF that ends the session.
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner(TimeProvider.System, code => exitCode = code).RunBroker(pipeName));
        try
        {
            await server.WaitForConnectionAsync(cts.Token);
            await server.DisposeAsync();
            await runTask.WaitAsync(cts.Token);
        }
        finally
        {
            await EnsureRunnerLeavesAsync(runTask, server);
        }

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_ClientDisconnectsDuringWatch_BrokenPipe_ExitsWithCode0()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The native USN watch hooks are Windows-only.");
        }

        // RunBroker serves through JournalBrokerHost.CreateDefault(), so fake the volume handle and
        // serve the watch from an idle native pipe - the test process is not elevated (the same
        // technique as JournalBrokerHostRealSeamsTests).
        FileUtilities.Hooks.GetVolumeHandle = _ => FakeVolumeHandle.Create();
        JournalQueryFake.Serve(journalIdentifier: 7, nextUsn: 200);

        // The native read stays parked before it is issued until the test has taken the client ends
        // away, then fails on the broken pipe. The host reports that failure as this drive's Error
        // frame, and the pipes are already gone by then - so the report write is the one that lands
        // on the broken pipe, which is what used to kill the child.
        using var idlePipe = await IdleUsnPipe.CreateAsync(gateReadNumber: 1);

        int? exitCode = null;

        using var cts = new CancellationTokenSource(HangGuard);
        using var timeoutRelease = cts.Token.Register(idlePipe.UnblockForCleanup); // never leak the parked read

        var (control, drive, runTask) = StartRunnerWithDrivePipe(code => exitCode = code);
        try
        {
            await AcceptDriveChannelAsync(control, drive, cts.Token);

            // Arm the live watch with a cursor at the journal tip: the host reports CaughtUp
            // over the still-healthy pipe, then parks in the mocked kernel wait above.
            await HostChannelHarness.WriteFrameAsync(drive,
                writer => BrokerProtocol.WriteStartWatch(writer, new UsnJournalCursor(7, 200)));
            var caughtUp = await HostChannelHarness.ReadFrameAsync(drive);
            Assert.AreEqual(BrokerFrameKind.CaughtUp, caughtUp?.Kind);

            // Reach the kernel wait before taking the client ends away. A watch that has not
            // got there yet observes the disconnect as a plain cancellation and never
            // attempts the write this test is about.
            await IdleUsnPipe.AwaitSignalAsync(idlePipe.BeforeIssue);

            // The consumer's process has now closed its ends while its watch was armed, so the
            // watch's next attempt to reach it fails on the broken pipe for real.
            await drive.DisposeAsync();
            await control.DisposeAsync();
            idlePipe.Break();
            idlePipe.ContinueIssue.Set();

            await runTask.WaitAsync(cts.Token);
        }
        finally
        {
            idlePipe.UnblockForCleanup();
            await EnsureRunnerLeavesAsync(runTask, drive, control);
        }

        Assert.AreEqual(0, exitCode);
        var calls = IdleUsnPipe.CallOrder();
        Assert.IsTrue(calls.Contains('C') && calls.Contains('F'), "The failed read is cancelled and freed.");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_ClientDisconnectsDuringArmAndScan_BrokenPipe_ExitsWithCode0()
    {
        // RunBroker serves through JournalBrokerHost.CreateDefault(), so mock the native seams
        // the scan's cursor arming uses - the test process is not elevated (the same technique
        // as the watch test above). The cursor query is where the host parks: it runs only
        // once the request frame has been read, and the armed-cursor reply is the next write.
        FileUtilities.Hooks.GetVolumeHandle = _ => FakeVolumeHandle.Create();
        var cursorQueried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCursorQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JournalQueryFake.Serve(() =>
        {
            cursorQueried.TrySetResult();
            releaseCursorQuery.Task.GetAwaiter().GetResult();
            return JournalQueryFake.Describe(journalIdentifier: 7, nextUsn: 200, maximumSize: 0, allocationDelta: 0);
        });

        int? exitCode = null;

        using var cts = new CancellationTokenSource(HangGuard);
        using var timeoutRelease = cts.Token.Register(() => releaseCursorQuery.TrySetResult());

        var (control, drive, runTask) = StartRunnerWithDrivePipe(code => exitCode = code);
        try
        {
            await AcceptDriveChannelAsync(control, drive, cts.Token);
            await HostChannelHarness.WriteFrameAsync(drive,
                writer => BrokerProtocol.WriteArmAndScan(writer, "mftlib-runner-scan-C"));

            // Reach the cursor query before taking the client ends away, so the reply that
            // follows is written to a pipe that is already broken for certain. A scan that has
            // not got there yet would answer over the healthy pipe and the disconnect would
            // land somewhere else.
            await cursorQueried.Task.WaitAsync(cts.Token);

            // The consumer's process has now closed its ends mid-scan, so the armed-cursor reply
            // fails on the broken pipe for real - the write that used to kill the child. The
            // native record scan itself never starts: the channel ends at that first reply.
            await drive.DisposeAsync();
            await control.DisposeAsync();
            releaseCursorQuery.TrySetResult();

            await runTask.WaitAsync(cts.Token);
        }
        finally
        {
            releaseCursorQuery.TrySetResult();
            await EnsureRunnerLeavesAsync(runTask, drive, control);
        }

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");
        int? exitCode = null;

        // The first diagnostics line parks the log writer on a gate, so lines are still queued when
        // the session fails. The clock is never advanced, so the flush's bound cannot end the wait.
        var sink = new TestGate();
        var appended = new List<string>();
        BrokerDiagnostics.Enable("broker", Path.GetTempPath());
        BrokerDiagnostics.Dependencies.InstallWriter(new BrokerDiagnosticsWriter(line =>
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
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner(clock, code => exitCode = code).RunBroker(pipeName));
        try
        {
            await server.WaitForConnectionAsync(cts.Token);

            // A frame of no known kind ends the session with InvalidDataException.
            await server.WriteAsync(new byte[] { 1, 0, 0, 0, 200 }, cts.Token);
            await server.FlushAsync(cts.Token);
            await sink.Entered.WaitAsync(cts.Token);
            await clock.FlushBoundStarted.WaitAsync(cts.Token);

            Assert.IsFalse(runTask.IsCompleted, "The runner must wait for the queued lines while the sink is held.");
            sink.Release();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runTask.WaitAsync(cts.Token));
            Assert.IsNull(exitCode);
            lock (appended)
            {
                Assert.IsTrue(appended.Any(line => line.Contains("frame read kind=200", StringComparison.Ordinal)),
                    "The line logged as the session failed must reach the log before the runner leaves.");
            }
        }
        finally
        {
            // Release the sink on every exit before draining the runner.
            sink.Release();
            await EnsureRunnerLeavesAsync(runTask, server);
        }
    }

    string _drivePipeName = string.Empty;

    // RunBroker blocks synchronously for the whole session, so it runs on a background thread
    // while this thread plays the non-elevated caller's side of the real control pipe and of one
    // real drive pipe, both created here the way the caller creates them.
    [SupportedOSPlatform("windows")]
    (NamedPipeServerStream Control, NamedPipeServerStream Drive, Task RunTask) StartRunnerWithDrivePipe(Action<int> exitProcess)
    {
        var controlName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");
        _drivePipeName = "mftlib-runner-drive-" + Guid.NewGuid().ToString("N");
        var control = new NamedPipeServerStream(
            controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var drive = new NamedPipeServerStream(
            _drivePipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner(TimeProvider.System, exitProcess).RunBroker(controlName));
        return (control, drive, runTask);
    }

    // Connects the control pipe, asks the broker to open drive C's channel on the drive pipe, and
    // returns once the broker has connected the drive pipe and answered ChannelOpened.
    async Task AcceptDriveChannelAsync(NamedPipeServerStream control, NamedPipeServerStream drive,
        CancellationToken cancellationToken)
    {
        await control.WaitForConnectionAsync(cancellationToken);
        await HostChannelHarness.WriteFrameAsync(control,
            writer => BrokerProtocol.WriteOpenChannel(writer, 1, 'C', _drivePipeName));
        await drive.WaitForConnectionAsync(cancellationToken);
        var opened = await HostChannelHarness.ReadFrameAsync(control);
        Assert.AreEqual(BrokerFrameKind.ChannelOpened, opened?.Kind);
    }

    // Close the caller's pipe ends and drain runner completion before seam reset.
    async Task EnsureRunnerLeavesAsync(Task runTask, params IAsyncDisposable[] pipes)
    {
        foreach (var pipe in pipes)
        {
            await pipe.DisposeAsync();
        }

        // A runner that faulted has left; only its failure to leave is a problem here.
        var left = runTask.ContinueWith(static _ => { }, TaskScheduler.Default);
        await _waitForRunnerExit(left);
    }

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

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task CleanupTimeout_DisposesPipesAndKeepsInjectedExit()
    {
        var runner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drive = new RecordingCleanupPipe();
        var control = new RecordingCleanupPipe();
        var timeout = new TimeoutException("injected runner drain timeout");
        _waitForRunnerExit = _ =>
        {
            Assert.IsTrue(drive.Disposed);
            Assert.IsTrue(control.Disposed);
            return Task.FromException(timeout);
        };

        try
        {
            var observed = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                EnsureRunnerLeavesAsync(runner.Task, drive, control));
            Assert.AreSame(timeout, observed);

            var exits = new List<int>();
            var delayedRunner = new DefaultElevatedEntryRunner(TimeProvider.System, exits.Add);
            await Cleanup();
            delayedRunner.RunBroker(null);
            CollectionAssert.AreEqual(new[] { 1 }, exits);
        }
        finally
        {
            runner.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnsureRunnerLeavesAsync_CompletedRunnerIsNotCleanupFailure(bool runnerFaulted)
    {
        var pipe = new RecordingCleanupPipe();
        var runner = runnerFaulted
            ? Task.FromException(new TimeoutException("runner body fault, not drain timeout"))
            : Task.CompletedTask;
        _waitForRunnerExit = left => left;

        await EnsureRunnerLeavesAsync(runner, pipe);

        Assert.IsTrue(pipe.Disposed);
    }

    sealed class RecordingCleanupPipe : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
