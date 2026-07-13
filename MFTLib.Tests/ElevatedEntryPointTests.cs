using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class ElevatedEntryPointTests
{
    // Records which runner method was invoked and with what arguments, so the
    // dispatch can be tested without spawning a real elevated process, pipe, or scan.
    sealed class RecordingRunner : IElevatedEntryRunner
    {
        public int BrokerCalls { get; private set; }
        public string? BrokerPipe { get; private set; }
        public bool BrokerOnce { get; private set; }

        public void RunBroker(string? pipeName, bool oneShot)
        {
            BrokerCalls++;
            BrokerPipe = pipeName;
            BrokerOnce = oneShot;
        }

        public int TotalCalls => BrokerCalls;
    }

    static readonly string[] BrokerWithOnceArgs = { "--broker", "--pipe", "mftlib-pipe-123", "--once" };
    static readonly string[] BrokerWithoutOnceArgs = { "--broker", "--pipe", "p" };
    static readonly string[] LeadingExecutablePathArgs = { @"C:\apps\SomeApp.exe", "--broker", "--pipe", "p" };
    static readonly string[] ScanOnlyArgs = { "--scan-only" };
    static readonly string[] BrokerWithDiagArgs = { "--broker", "--pipe", "p", "--diag" };
    static readonly string[] PipeFlagWithNoValueArgs = { "--broker", "--pipe" };
    static readonly string[] NoPipeFlagArgs = { "--broker" };

    [TestMethod]
    public void TryHandle_BrokerMode_RoutesPipeName_AndReturnsTrue()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(BrokerWithOnceArgs, runner);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, runner.BrokerCalls);
        Assert.AreEqual("mftlib-pipe-123", runner.BrokerPipe);
        Assert.IsTrue(runner.BrokerOnce);
    }

    [TestMethod]
    public void TryHandle_BrokerMode_WithoutOnce_DefaultsToPersistent()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(BrokerWithoutOnceArgs, runner);

        Assert.IsTrue(handled);
        Assert.IsFalse(runner.BrokerOnce);
    }

    [TestMethod]
    public void TryHandle_IgnoresLeadingExecutablePath()
    {
        // A caller might pass Environment.GetCommandLineArgs(), whose first element
        // is the executable path. The dispatch must scan past it to find the mode flag.
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(LeadingExecutablePathArgs, runner);

        Assert.IsTrue(handled);
        Assert.AreEqual("p", runner.BrokerPipe);
    }

    [TestMethod]
    public void TryHandle_UnknownArgs_ReturnsFalse_AndInvokesNothing()
    {
        var runner = new RecordingRunner();

        Assert.IsFalse(ElevatedEntryPoint.TryHandle(ScanOnlyArgs, runner));
        Assert.IsFalse(ElevatedEntryPoint.TryHandle(Array.Empty<string>(), runner));
        Assert.AreEqual(0, runner.TotalCalls);
    }

    [TestCleanup]
    public void Cleanup() => BrokerDiagnostics.ResetToDefaults();

    [TestMethod]
    public void TryHandle_BrokerModeWithDiagFlag_EnablesDiagnostics()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(BrokerWithDiagArgs, runner);

        Assert.IsTrue(handled);
        // Verify Enable("broker") actually ran: force a log line through it via a
        // temp LogDirectory (BrokerDiagnostics itself is exercised by its own tests).
        var tempDir = Path.Combine(Path.GetTempPath(), "ElevatedEntryPointDiagTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalDirectory = BrokerDiagnostics.LogDirectory;
        try
        {
            BrokerDiagnostics.LogDirectory = tempDir;
            BrokerDiagnostics.Log("diag-enabled-check");
            Assert.IsTrue(File.Exists(Path.Combine(tempDir, "broker-diagnostics.log")));
        }
        finally
        {
            BrokerDiagnostics.LogDirectory = originalDirectory;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void TryHandle_BrokerMode_PipeFlagWithNoValue_PipeNameIsNull()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(PipeFlagWithNoValueArgs, runner);

        Assert.IsTrue(handled);
        Assert.IsNull(runner.BrokerPipe);
    }

    [TestMethod]
    public void TryHandle_BrokerMode_NoPipeFlagAtAll_PipeNameIsNull()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(NoPipeFlagArgs, runner);

        Assert.IsTrue(handled);
        Assert.IsNull(runner.BrokerPipe);
    }
}
