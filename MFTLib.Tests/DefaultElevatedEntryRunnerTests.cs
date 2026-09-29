using System.IO.Pipes;
using System.Runtime.Versioning;
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
        DefaultElevatedEntryRunner._diagnosticsFlushTimeout = Timeout.InfiniteTimeSpan;

        // The first diagnostics line parks the log writer, so a line is still queued when the
        // session fails; the runner may leave only once its flush has seen that line through.
        var appendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BrokerDiagnostics.Enable("broker");
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(_ =>
        {
            appendEntered.TrySetResult();
            releaseAppend.Task.Wait(TimeSpan.FromSeconds(10));
        }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(pipeName));
        await server.WaitForConnectionAsync(cts.Token);

        // A frame of no known kind ends the session with InvalidDataException.
        await server.WriteAsync(new byte[] { 1, 0, 0, 0, 200 }, cts.Token);
        await server.FlushAsync(cts.Token);
        await appendEntered.Task.WaitAsync(cts.Token);

        var finished = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token));
        Assert.AreNotSame(runTask, finished, "The runner left while a diagnostics line was still queued.");

        releaseAppend.TrySetResult();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => runTask.WaitAsync(cts.Token));
        Assert.IsNull(exitCode);
    }
}
