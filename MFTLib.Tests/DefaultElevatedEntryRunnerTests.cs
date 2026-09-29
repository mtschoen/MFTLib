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
}
