using System.Buffers;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class DefaultElevatedEntryRunnerTests
{
    [TestCleanup]
    public void Cleanup() => DefaultElevatedEntryRunner.ResetToDefaults();

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void RunBroker_NullPipeName_ExitsWithCode1_WithoutConnecting()
    {
        int? exitCode = null;
        DefaultElevatedEntryRunner.ExitProcess = code => exitCode = code;

        new DefaultElevatedEntryRunner().RunBroker(null, oneShot: false);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task RunBroker_ValidPipeName_ConnectsRealNamedPipe_ServesUntilShutdown_ExitsWithCode0()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        int? exitCode = null;
        DefaultElevatedEntryRunner.ExitProcess = code => exitCode = code;

        // RunBroker blocks synchronously (.GetAwaiter().GetResult()) for the whole
        // session, so drive it from a background thread while this thread plays the
        // non-elevated caller's side of the real named pipe.
        var runTask = Task.Run(() => new DefaultElevatedEntryRunner().RunBroker(pipeName, oneShot: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.WaitForConnectionAsync(cts.Token);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await server.WriteAsync(shutdown.WrittenMemory, cts.Token);
        await server.FlushAsync(cts.Token);

        await runTask.WaitAsync(cts.Token);

        Assert.AreEqual(0, exitCode);
    }
}
