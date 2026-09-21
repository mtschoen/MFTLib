using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize] // references the process-wide MFTLibNative/FileUtilities delegate seams below
public class DefaultElevatedEntryRunnerTests
{
    [TestCleanup]
    public void Cleanup()
    {
        DefaultElevatedEntryRunner.ResetToDefaults();
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void RunBroker_NullPipeName_ExitsWithCode1_WithoutConnecting()
    {
        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        new DefaultElevatedEntryRunner().RunBroker(null, false);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_ValidPipeName_ConnectsRealNamedPipe_ServesUntilShutdown_ExitsWithCode0()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        // RunBroker blocks synchronously (.GetAwaiter().GetResult()) for the whole
        // session, so drive it from a background thread while this thread plays the
        // non-elevated caller's side of the real named pipe.
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(pipeName, false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.WaitForConnectionAsync(cts.Token);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await server.WriteAsync(shutdown.WrittenMemory, cts.Token);
        await server.FlushAsync(cts.Token);

        await runTask.WaitAsync(cts.Token);

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

        // The kernel wait stays blocked until the test has taken the client end away, then
        // comes back with a journal read failure. The host reports that failure as this
        // drive's Error frame, and the pipe is already gone by then - so the report write is
        // the one that lands on the broken pipe, which is what used to kill the child.
        var watchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MFTLibNative._watchUsnJournalBatchCancelable = (_, _, journalId, _) =>
        {
            watchEntered.TrySetResult();
            releaseWatch.Task.GetAwaiter().GetResult();
            return BuildWatchFailureResult(journalId, "FSCTL_READ_USN_JOURNAL watch failed");
        };

        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");

        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var timeoutRelease = cts.Token.Register(() => releaseWatch.TrySetResult()); // never leak the blocked mock

        // RunBroker blocks synchronously for the whole session, so drive it from a background
        // thread while this thread plays the non-elevated caller's side of the real named pipe.
        // Leaving the server's scope below closes that end, which is the client disconnect.
        Task runTask;
        await using (var server = new NamedPipeServerStream(
                         pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                         PipeOptions.Asynchronous))
        {
            runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(pipeName, false));
            await server.WaitForConnectionAsync(cts.Token);

            // Arm the live watch with a cursor at the journal tip: the host reports CaughtUp
            // over the still-healthy pipe, then parks in the mocked kernel wait above.
            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteStartWatch(request, "C:7:200:1");
            await server.WriteAsync(request.WrittenMemory, cts.Token);
            await server.FlushAsync(cts.Token);

            var caughtUp = await ReadOneFrameAsync(server).WaitAsync(cts.Token);
            Assert.AreEqual(BrokerFrameKind.CaughtUp, caughtUp.Kind);

            // Reach the kernel wait before taking the client end away. A watch that has not
            // got there yet observes the disconnect as a plain cancellation and never
            // attempts the write this test is about.
            await watchEntered.Task.WaitAsync(cts.Token);
        }

        // The consumer's process has now closed its end while its watch was armed, so the
        // watch's next attempt to reach it fails on the broken pipe for real.
        releaseWatch.TrySetResult();

        await runTask.WaitAsync(cts.Token);

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

        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");

        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var timeoutRelease = cts.Token.Register(() => releaseCursorQuery.TrySetResult());

        // RunBroker blocks synchronously for the whole session, so drive it from a background
        // thread while this thread plays the non-elevated caller's side of the real named pipe.
        // Leaving the server's scope below closes that end, which is the client disconnect.
        Task runTask;
        await using (var server = new NamedPipeServerStream(
                         pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                         PipeOptions.Asynchronous))
        {
            runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(pipeName, false));
            await server.WaitForConnectionAsync(cts.Token);

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-runner-scan-C");
            await server.WriteAsync(request.WrittenMemory, cts.Token);
            await server.FlushAsync(cts.Token);

            // Reach the cursor query before taking the client end away, so the reply that
            // follows is written to a pipe that is already broken for certain. A scan that has
            // not got there yet would answer over the healthy pipe and the disconnect would
            // land somewhere else.
            await cursorQueried.Task.WaitAsync(cts.Token);
        }

        // The consumer's process has now closed its end mid-scan, so the armed-cursor reply
        // fails on the broken pipe for real - the write that used to kill the child. The
        // native record scan itself never starts: the session ends at that first reply.
        releaseCursorQuery.TrySetResult();

        await runTask.WaitAsync(cts.Token);

        Assert.AreEqual(0, exitCode);
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
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
}
