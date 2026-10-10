using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class ElevatedEntryPointTests
{
    static readonly string InvalidDiagnosticsDirectory = Path.Combine(Path.GetTempPath(), "Invalid diagnostics " + Guid.NewGuid().ToString("N"));
    static readonly string ClientLogPath = Path.Combine(Path.GetTempPath(), "Client diagnostics " + Guid.NewGuid().ToString("N"),
        "broker-diagnostics.log");
    static readonly string[] BrokerArgs = ["--broker", "--pipe", "mftlib-pipe-123"];
    static readonly string[] LeadingExecutablePathArgs = [@"C:\apps\SomeApp.exe", "--broker", "--pipe", "p"];
    static readonly string[] ScanOnlyArgs = ["--scan-only"];
    static readonly string[] BrokerWithDiagLogArgs =
        ["--broker", "--pipe", "p", "--diag", "--diag-log", ClientLogPath];
    static readonly string[] BrokerWithDiagIncludeSelfArgs =
        ["--broker", "--pipe", "p", "--diag", "--diag-log", ClientLogPath, "--diag-include-self"];
    static readonly string[] DiagLogWithoutDiagArgs =
        ["--broker", "--pipe", "p", "--diag-log", @"C:\client-app\broker-diagnostics.log"];
    static readonly string[] PipeFlagWithNoValueArgs = ["--broker", "--pipe"];
    static readonly string[] NoPipeFlagArgs = ["--broker"];

    [TestMethod]
    public void TryHandle_BrokerMode_RoutesPipeName_AndReturnsTrue()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(BrokerArgs, runner);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, runner.BrokerCalls);
        Assert.AreEqual("mftlib-pipe-123", runner.BrokerPipe);
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

    [TestMethod]
    public void TryHandle_WithoutARunner_NormalLaunch_ReturnsFalseWithoutStartingABroker()
    {
        // The public overload runs the production runner, which exits the process, so only a
        // launch that carries no mode flag can be exercised here.
        Assert.IsFalse(ElevatedEntryPoint.TryHandle(ScanOnlyArgs));
        Assert.IsFalse(ElevatedEntryPoint.TryHandle([]));
    }

    [TestCleanup]
    public void Cleanup()
    {
        BrokerDiagnostics.ResetToDefaults();
    }

    [DataTestMethod]
    [DynamicData(nameof(ClientDiagnosticsPathKinds), DynamicDataSourceType.Method)]
    public async Task TryHandle_BrokerModeWithDiagFlag_EnablesDiagnostics(bool extendedLength, string directoryName)
    {
        var runner = new RecordingRunner();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Elevated child diagnostics " + Guid.NewGuid().ToString("N"));
        var cleanupDirectory = extendedLength ? @"\\?\" + temporaryDirectory : temporaryDirectory;
        var clientDirectory = Path.Combine(cleanupDirectory, directoryName);
        var originalExit = DefaultElevatedEntryRunner._exitProcess;
        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;
        try
        {
            Directory.CreateDirectory(clientDirectory);
            BrokerDiagnostics.Enable("client", clientDirectory);
            var suppliedPath = BrokerDiagnostics.LogPath;
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "client-enabled-check");
            await BrokerDiagnostics.FlushAsync(CancellationToken.None);
            BrokerDiagnostics.ResetToDefaults();
            var handled = ElevatedEntryPoint.TryHandle(
                ["--broker", "--pipe", "p", "--diag", "--diag-log", suppliedPath], runner);

            Assert.IsTrue(handled);
            Assert.AreEqual(1, runner.BrokerCalls);
            Assert.IsNull(exitCode);
            Assert.AreEqual(suppliedPath, BrokerDiagnostics.LogPath,
                "The child must configure the supplied directory before any diagnostics write.");
            Assert.AreEqual(suppliedPath, BrokerDiagnostics.ClientLogPath);
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "diag-enabled-check");
            await BrokerDiagnostics.FlushAsync(CancellationToken.None);
            var artifact = await File.ReadAllTextAsync(suppliedPath);
            StringAssert.Contains(artifact, "[broker:");
            StringAssert.Contains(artifact, "client-enabled-check");
            StringAssert.Contains(artifact, "diag-enabled-check");
        }
        finally
        {
            BrokerDiagnostics.ResetToDefaults();
            DefaultElevatedEntryRunner._exitProcess = originalExit;
            Directory.Delete(cleanupDirectory, true);
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

    [DataTestMethod]
    [DynamicData(nameof(InvalidDiagnosticsArguments), DynamicDataSourceType.Method)]
    public void TryHandle_InvalidDiagnosticsPath_HandlesModeWithoutRunningBroker(string[] arguments)
    {
        var runner = new RecordingRunner();
        var originalExit = DefaultElevatedEntryRunner._exitProcess;
        int? exitCode = null;
        DefaultElevatedEntryRunner._exitProcess = code => exitCode = code;
        Directory.CreateDirectory(Path.Combine(InvalidDiagnosticsDirectory, BrokerDiagnostics.LogFileName));
        try
        {
            Assert.IsTrue(ElevatedEntryPoint.TryHandle(arguments, runner));
            Assert.AreEqual(0, runner.TotalCalls);
            Assert.AreEqual(1, exitCode);
        }
        finally
        {
            DefaultElevatedEntryRunner._exitProcess = originalExit;
            Directory.Delete(InvalidDiagnosticsDirectory, true);
        }
    }

    public static IEnumerable<object[]> InvalidDiagnosticsArguments()
    {
        yield return [new[] { "--broker", "--diag" }];
        yield return [new[] { "--broker", "--diag", "--diag-log" }];
        string[] paths = ["", " ", "relative/broker-diagnostics.log", "--diag-include-self",
            Path.Combine(Path.GetTempPath(), "bad\0path"), Path.GetPathRoot(Path.GetTempPath())!, Path.GetTempPath(),
            Path.TrimEndingDirectorySeparator(InvalidDiagnosticsDirectory),
            Path.Combine(InvalidDiagnosticsDirectory, BrokerDiagnostics.LogFileName),
            Path.Combine(InvalidDiagnosticsDirectory, "."),
            Path.Combine(InvalidDiagnosticsDirectory, ".."),
            Path.Combine(InvalidDiagnosticsDirectory, "wrong-name.log"),
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

            yield return [new[] { "--broker", "--diag", "--diag-log", @"C:relative.log" }];
            yield return [new[] { "--broker", "--diag", "--diag-log", Path.Combine(Path.GetTempPath(), "bad|name.log") }];
            yield return [new[] { "--broker", "--diag", "--diag-log", Path.Combine(Path.GetTempPath(), "bad?directory", "broker-diagnostics.log") }];
        }
    }

    [TestMethod]
    public void TryHandle_BrokerModeWithDiagLog_ForwardsClientLogPathToDiagnostics()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(BrokerWithDiagLogArgs, runner);

        Assert.IsTrue(handled);
        Assert.AreEqual(ClientLogPath, BrokerDiagnostics.ClientLogPath);
        Assert.IsFalse(BrokerDiagnostics.IncludeSelfEntries);
    }

    [TestMethod]
    public void TryHandle_BrokerModeWithDiagIncludeSelf_SetsIncludeSelfEntries()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(BrokerWithDiagIncludeSelfArgs, runner);

        Assert.IsTrue(handled);
        Assert.IsTrue(BrokerDiagnostics.IncludeSelfEntries);
    }

    [TestMethod]
    public void TryHandle_DiagLogWithoutDiag_LeavesClientLogPathNull()
    {
        var runner = new RecordingRunner();

        var handled = ElevatedEntryPoint.TryHandle(DiagLogWithoutDiagArgs, runner);

        Assert.IsTrue(handled);
        Assert.IsNull(BrokerDiagnostics.ClientLogPath);
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

    // Records which runner method was invoked and with what arguments, so the
    // dispatch can be tested without spawning a real elevated process, pipe, or scan.
    sealed class RecordingRunner : IElevatedEntryRunner
    {
        public int BrokerCalls { get; private set; }
        public string? BrokerPipe { get; private set; }

        public int TotalCalls => BrokerCalls;

        public void RunBroker(string? controlPipeName)
        {
            BrokerCalls++;
            BrokerPipe = controlPipeName;
        }
    }
}
