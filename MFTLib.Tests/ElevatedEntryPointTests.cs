using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class ElevatedEntryPointTests
{
    static readonly string InvalidDiagnosticsDirectory = Path.Combine(Path.GetTempPath(), "Invalid diagnostics " + Guid.NewGuid().ToString("N"));
    static readonly string ClientLogPath = Path.Combine(Path.GetTempPath(), "Client diagnostics " + Guid.NewGuid().ToString("N"),
        "broker-diagnostics.ui.log");
    static readonly string[] BrokerArgs = ["--broker", "--pipe", "mftlib-pipe-123"];
    static readonly string[] LeadingExecutablePathArgs = [@"C:\apps\SomeApp.exe", "--broker", "--pipe", "p"];
    static readonly string[] ScanOnlyArgs = ["--scan-only"];
    static readonly string[] BrokerWithDiagLogArgs =
        ["--broker", "--pipe", "p", "--diag", "--diag-log", ClientLogPath];
    static readonly string[] BrokerWithDiagIncludeSelfArgs =
        ["--broker", "--pipe", "p", "--diag", "--diag-log", ClientLogPath, "--diag-include-self"];
    static readonly string[] DiagLogWithoutDiagArgs =
        ["--broker", "--pipe", "p", "--diag-log", @"C:\client-app\broker-diagnostics.ui.log"];
    static readonly string[] PipeFlagWithNoValueArgs = ["--broker", "--pipe"];
    static readonly string[] NoPipeFlagArgs = ["--broker"];

    [TestMethod]
    public void RunBrokerIfRequested_BrokerMode_RoutesPipeName_AndReturnsTrue()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.RunBrokerIfRequested(BrokerArgs, runner, runner.RecordExit);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, runner.BrokerCalls);
        Assert.AreEqual("mftlib-pipe-123", runner.BrokerPipe);
        Assert.IsEmpty(runner.ExitCodes);
    }

    [TestMethod]
    public void RunBrokerIfRequested_IgnoresLeadingExecutablePath()
    {
        // A caller might pass Environment.GetCommandLineArgs(), whose first element
        // is the executable path. The dispatch must scan past it to find the mode flag.
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.RunBrokerIfRequested(LeadingExecutablePathArgs, runner, runner.RecordExit);

        Assert.IsTrue(handled);
        Assert.AreEqual("p", runner.BrokerPipe);
    }

    [TestMethod]
    public void RunBrokerIfRequested_UnknownArgs_ReturnsFalse_AndInvokesNothing()
    {
        var runner = new RecordingRunner();

        Assert.IsFalse(ElevatedEntryPoint.RunBrokerIfRequested(ScanOnlyArgs, runner, runner.RecordExit));
        Assert.IsFalse(ElevatedEntryPoint.RunBrokerIfRequested([], runner, runner.RecordExit));
        Assert.AreEqual(0, runner.TotalCalls);
    }

    [TestMethod]
    public void RunBrokerIfRequested_WithoutARunner_NormalLaunch_ReturnsFalseWithoutStartingABroker()
    {
        // The public overload runs the production runner, which exits the process, so only a
        // launch that carries no mode flag can be exercised here.
        Assert.IsFalse(ElevatedEntryPoint.RunBrokerIfRequested(ScanOnlyArgs));
        Assert.IsFalse(ElevatedEntryPoint.RunBrokerIfRequested([]));
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        BrokerDiagnostics.Dependencies.ResetToDefaults();
    }

    [TestMethod]
    [DynamicData(nameof(ClientDiagnosticsPathKinds))]
    public async Task RunBrokerIfRequested_BrokerModeWithDiagFlag_EnablesDiagnostics(bool extendedLength, string directoryName)
    {
        var runner = new RecordingRunner();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Elevated child diagnostics " + Guid.NewGuid().ToString("N"));
        var cleanupDirectory = extendedLength ? @"\\?\" + temporaryDirectory : temporaryDirectory;
        var clientDirectory = Path.Combine(cleanupDirectory, directoryName);
        try
        {
            Directory.CreateDirectory(clientDirectory);
            BrokerDiagnostics.Enable("Client-2_test", clientDirectory);
            var suppliedPath = BrokerDiagnostics.LogPath;
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "client-enabled-check");
            await BrokerDiagnostics.FlushAsync(CancellationToken.None);
            BrokerDiagnostics.Dependencies.ResetToDefaults();
            var handled = ElevatedEntryPoint.RunBrokerIfRequested(
                ["--broker", "--pipe", "p", "--diag", "--diag-log", suppliedPath], runner, runner.RecordExit);

            Assert.IsTrue(handled);
            Assert.AreEqual(1, runner.BrokerCalls);
            Assert.IsEmpty(runner.ExitCodes);
            var brokerPath = Path.Combine(clientDirectory, "broker-diagnostics.broker.log");
            Assert.AreEqual(brokerPath, BrokerDiagnostics.LogPath,
                "The child must configure the supplied directory before any diagnostics write.");
            Assert.AreEqual(suppliedPath, BrokerDiagnostics.ClientLogPath);
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "diag-enabled-check");
            await BrokerDiagnostics.FlushAsync(CancellationToken.None);
            var artifact = await File.ReadAllTextAsync(brokerPath, TestContext.CancellationToken);
            Assert.Contains("[broker:", artifact);
            Assert.IsFalse(artifact.Contains("client-enabled-check", StringComparison.Ordinal));
            Assert.Contains("client-enabled-check", await File.ReadAllTextAsync(suppliedPath, TestContext.CancellationToken));
            Assert.Contains("diag-enabled-check", artifact);
        }
        finally
        {
            try
            {
                try
                {
                    await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
                }
                finally
                {
                    BrokerDiagnostics.Dependencies.ResetToDefaults();
                }
            }
            finally
            {
                Directory.Delete(cleanupDirectory, true);
            }
        }
    }

    public static IEnumerable<object[]> ClientDiagnosticsPathKinds()
    {
        yield return [false, ""];
        if (OperatingSystem.IsWindows())
        {
            yield return [true, ""];
            foreach (var directoryName in new[] { "NUL", "CON", "COM1", "NUL.txt", "LPT\u00b9" })
            {
                yield return [true, directoryName];
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(InvalidDiagnosticsArguments))]
    public void RunBrokerIfRequested_InvalidDiagnosticsPath_HandlesModeWithoutRunningBroker(string[] arguments)
    {
        var runner = new RecordingRunner();
        Directory.CreateDirectory(Path.Combine(InvalidDiagnosticsDirectory, BrokerDiagnostics.LogFileName));
        try
        {
            Assert.IsTrue(ElevatedEntryPoint.RunBrokerIfRequested(arguments, runner, runner.RecordExit));
            Assert.AreEqual(0, runner.TotalCalls);
            Assert.AreSequenceEqual([1], runner.ExitCodes);
        }
        finally
        {
            Directory.Delete(InvalidDiagnosticsDirectory, true);
        }
    }

    public static IEnumerable<object[]> InvalidDiagnosticsArguments()
    {
        yield return [new[] { "--broker", "--diag" }];
        yield return [new[] { "--broker", "--diag", "--diag-log" }];
        string[] paths = ["", " ", "relative/broker-diagnostics.ui.log", "--diag-include-self",
            Path.Combine(Path.GetTempPath(), "bad\0path"), Path.GetPathRoot(Path.GetTempPath())!, Path.GetTempPath(),
            Path.TrimEndingDirectorySeparator(InvalidDiagnosticsDirectory),
            Path.Combine(InvalidDiagnosticsDirectory, BrokerDiagnostics.LogFileName),
            Path.Combine(InvalidDiagnosticsDirectory, "."),
            Path.Combine(InvalidDiagnosticsDirectory, ".."),
            Path.Combine(InvalidDiagnosticsDirectory, "wrong-name.log"),
            Path.Combine(InvalidDiagnosticsDirectory, "broker-diagnostics.log"),
            Path.Combine(InvalidDiagnosticsDirectory, "broker-diagnostics..log"),
            Path.Combine(InvalidDiagnosticsDirectory, "broker-diagnostics.ui.role.log"),
            Path.Combine(InvalidDiagnosticsDirectory, "broker-diagnostics.ui role.log"),
            Path.Combine(InvalidDiagnosticsDirectory, "broker-diagnostics.ui!.log"),
            Path.Combine(InvalidDiagnosticsDirectory, "broker-diagnostics.ui.LOG"),
            Path.Combine(InvalidDiagnosticsDirectory, ".", BrokerDiagnostics.LogFileName),
            Path.Combine(InvalidDiagnosticsDirectory, "..", BrokerDiagnostics.LogFileName)];
        foreach (var path in paths)
        {
            yield return [new[] { "--broker", "--diag", "--diag-log", path }];
        }

        if (OperatingSystem.IsWindows())
        {
            yield return [new[] { "--broker", "--diag", "--diag-log", Path.Combine(InvalidDiagnosticsDirectory, new string('a', 40000), BrokerDiagnostics.LogFileName) }];
            foreach (var reservedName in new[] { "NUL", "CON", "COM1", "NUL.txt" })
            {
                yield return [new[] { "--broker", "--diag", "--diag-log", Path.Combine(InvalidDiagnosticsDirectory, reservedName, BrokerDiagnostics.LogFileName) }];
            }

            yield return [new[] { "--broker", "--diag", "--diag-log", "C:relative.log" }];
            yield return [new[] { "--broker", "--diag", "--diag-log", Path.Combine(Path.GetTempPath(), "bad|name.log") }];
            yield return [new[] { "--broker", "--diag", "--diag-log", Path.Combine(Path.GetTempPath(), "bad?directory", "broker-diagnostics.ui.log") }];
        }
    }

    [TestMethod]
    public void RunBrokerIfRequested_BrokerModeWithDiagLog_ForwardsClientLogPathToDiagnostics()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.RunBrokerIfRequested(BrokerWithDiagLogArgs, runner, runner.RecordExit);

        Assert.IsTrue(handled);
        Assert.AreEqual(ClientLogPath, BrokerDiagnostics.ClientLogPath);
        Assert.IsFalse(BrokerDiagnostics.IncludeSelfEntries);
    }

    [TestMethod]
    public void RunBrokerIfRequested_BrokerModeWithDiagIncludeSelf_SetsIncludeSelfEntries()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.RunBrokerIfRequested(BrokerWithDiagIncludeSelfArgs, runner, runner.RecordExit);

        Assert.IsTrue(handled);
        Assert.IsTrue(BrokerDiagnostics.IncludeSelfEntries);
    }

    [TestMethod]
    public void RunBrokerIfRequested_DiagLogWithoutDiag_LeavesClientLogPathNull()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.RunBrokerIfRequested(DiagLogWithoutDiagArgs, runner, runner.RecordExit);

        Assert.IsTrue(handled);
        Assert.IsNull(BrokerDiagnostics.ClientLogPath);
    }

    [TestMethod]
    public void RunBrokerIfRequested_BrokerMode_PipeFlagWithNoValue_PipeNameIsNull()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.RunBrokerIfRequested(PipeFlagWithNoValueArgs, runner, runner.RecordExit);

        Assert.IsTrue(handled);
        Assert.IsNull(runner.BrokerPipe);
    }

    [TestMethod]
    public void RunBrokerIfRequested_BrokerMode_NoPipeFlagAtAll_PipeNameIsNull()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.RunBrokerIfRequested(NoPipeFlagArgs, runner, runner.RecordExit);

        Assert.IsTrue(handled);
        Assert.IsNull(runner.BrokerPipe);
    }

    // Records which runner method was invoked and with what arguments, so the
    // dispatch can be tested without spawning a real elevated process, pipe, or scan.
    sealed class RecordingRunner : IElevatedEntryRunner
    {
        public List<int> ExitCodes { get; } = [];
        public void RecordExit(int code) => ExitCodes.Add(code);

        public int BrokerCalls { get; private set; }
        public string? BrokerPipe { get; private set; }

        public int TotalCalls => BrokerCalls;

        public void RunBroker(string? controlPipeName)
        {
            BrokerCalls++;
            BrokerPipe = controlPipeName;
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
