using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize] // replaces the process-wide DefaultElevatedEntryRunner._exitProcess seam and the MFTLibNative/FileUtilities delegate seams
public class DefaultElevatedEntryRunnerTests
{
    // Set when a runner thread was still serving after its pipes closed. Restoring the real
    // Environment.Exit then would let that thread kill the test host, so the fake stays.
    bool _runnerStillServing;

    [TestCleanup]
    public void Cleanup()
    {
        if (_runnerStillServing)
        {
            DefaultElevatedEntryRunner._exitProcess = _ => { };
        }
        else
        {
            DefaultElevatedEntryRunner.ResetToDefaults();
        }

        BrokerDiagnostics.ResetToDefaults();
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
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
    public async Task RunBroker_ValidControlPipe_ServesUntilControlCloses_ExitsWithCode0()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");

        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        using var cts = new CancellationTokenSource(HangGuard);

        // RunBroker blocks synchronously (.GetAwaiter().GetResult()) for the whole session, so
        // drive it from a background thread while this thread plays the non-elevated caller's
        // side of the real control pipe. Closing that end is the control EOF that ends the session.
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(pipeName));
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
        // RunBroker serves through JournalBrokerHost.CreateDefault(), so mock the native
        // seams its watch path uses - the test process is not elevated (the same technique
        // as JournalBrokerHostRealSeamsTests).
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        FileUtilities._getWatchVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._queryUsnJournal = _ =>
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
            Marshal.StructureToPtr(new UsnJournalInfoNative { JournalId = 7, NextUsn = 200 }, pointer, false);
            return pointer;
        };
        MFTLibNative._freeUsnJournalInfo = Marshal.FreeHGlobal;
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;
        MFTLibNative._cancelUsnJournalWatch = _ => true;

        // The kernel wait stays blocked until the test has taken the client ends away, then
        // comes back with a journal read failure. The host reports that failure as this
        // drive's Error frame, and the pipes are already gone by then - so the report write is
        // the one that lands on the broken pipe, which is what used to kill the child.
        var watchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MFTLibNative._watchUsnJournalBatchCancelable = (_, _, journalId, _) =>
        {
            watchEntered.TrySetResult();
            releaseWatch.Task.GetAwaiter().GetResult();
            return BuildWatchFailureResult(journalId, "FSCTL_READ_USN_JOURNAL watch failed");
        };

        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        using var cts = new CancellationTokenSource(HangGuard);
        using var timeoutRelease = cts.Token.Register(() => releaseWatch.TrySetResult()); // never leak the blocked mock

        var (control, drive, runTask) = StartRunnerWithDrivePipe();
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
            await watchEntered.Task.WaitAsync(cts.Token);

            // The consumer's process has now closed its ends while its watch was armed, so the
            // watch's next attempt to reach it fails on the broken pipe for real.
            await drive.DisposeAsync();
            await control.DisposeAsync();
            releaseWatch.TrySetResult();

            await runTask.WaitAsync(cts.Token);
        }
        finally
        {
            releaseWatch.TrySetResult();
            await EnsureRunnerLeavesAsync(runTask, drive, control);
        }

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_ClientDisconnectsDuringArmAndScan_BrokenPipe_ExitsWithCode0()
    {
        // RunBroker serves through JournalBrokerHost.CreateDefault(), so mock the native seams
        // the scan's cursor arming uses - the test process is not elevated (the same technique
        // as the watch test above). The cursor query is where the host parks: it runs only
        // once the request frame has been read, and the armed-cursor reply is the next write.
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        var cursorQueried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCursorQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MFTLibNative._queryUsnJournal = _ =>
        {
            cursorQueried.TrySetResult();
            releaseCursorQuery.Task.GetAwaiter().GetResult();
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
            Marshal.StructureToPtr(new UsnJournalInfoNative { JournalId = 7, NextUsn = 200 }, pointer, false);
            return pointer;
        };
        MFTLibNative._freeUsnJournalInfo = Marshal.FreeHGlobal;
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;

        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        using var cts = new CancellationTokenSource(HangGuard);
        using var timeoutRelease = cts.Token.Register(() => releaseCursorQuery.TrySetResult());

        var (control, drive, runTask) = StartRunnerWithDrivePipe();
        try
        {
            await AcceptDriveChannelAsync(control, drive, cts.Token);
            await HostChannelHarness.WriteFrameAsync(drive,
                writer => BrokerProtocol.WriteArmAndScan(writer, "mftlib-runner-scan-C", BrokerScanProfile.Full));

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

    string _drivePipeName = string.Empty;

    // RunBroker blocks synchronously for the whole session, so it runs on a background thread
    // while this thread plays the non-elevated caller's side of the real control pipe and of one
    // real drive pipe, both created here the way the caller creates them.
    [SupportedOSPlatform("windows")]
    (NamedPipeServerStream Control, NamedPipeServerStream Drive, Task RunTask) StartRunnerWithDrivePipe()
    {
        var controlName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");
        _drivePipeName = "mftlib-runner-drive-" + Guid.NewGuid().ToString("N");
        var control = new NamedPipeServerStream(
            controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var drive = new NamedPipeServerStream(
            _drivePipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(controlName));
        return (control, drive, runTask);
    }

    // Connects the control pipe, asks the broker to open drive C's channel on the drive pipe, and
    // returns once the broker has connected the drive pipe and answered ChannelOpened.
    async Task AcceptDriveChannelAsync(NamedPipeServerStream control, NamedPipeServerStream drive,
        CancellationToken cancellationToken)
    {
        await control.WaitForConnectionAsync(cancellationToken);
        await HostChannelHarness.WriteFrameAsync(control,
            writer => BrokerProtocol.WriteOpenChannel(writer, 1, "C", _drivePipeName));
        await drive.WaitForConnectionAsync(cancellationToken);
        var opened = await HostChannelHarness.ReadFrameAsync(control);
        Assert.AreEqual(BrokerFrameKind.ChannelOpened, opened?.Kind);
    }

    // Closes the caller's ends, which is what ends a session still being served, and waits for
    // the runner thread to leave. A runner that does not leave in time is remembered, so cleanup
    // keeps the fake exit seam in place instead of letting that thread kill the test host.
    async Task EnsureRunnerLeavesAsync(Task runTask, params IAsyncDisposable[] pipes)
    {
        foreach (var pipe in pipes)
        {
            await pipe.DisposeAsync();
        }

        try
        {
            await runTask.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            _runnerStillServing = true;
            throw;
        }
    }

    static IntPtr BuildWatchFailureResult(ulong journalId, string errorMessage)
    {
        var nativeResult = new UsnJournalResultNative
        {
            EntryCount = 0,
            Entries = IntPtr.Zero,
            NextUsn = 0,
            JournalId = journalId,
            ErrorMessage = errorMessage
        };
        var resultPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPointer, false);
        return resultPointer;
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
}
