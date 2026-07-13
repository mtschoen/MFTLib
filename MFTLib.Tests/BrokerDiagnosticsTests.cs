using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerDiagnosticsTests
{
    string _temporaryRoot = null!;
    string _originalLogDirectory = null!;

    [TestInitialize]
    public void Setup()
    {
        _originalLogDirectory = BrokerDiagnostics.LogDirectory;
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "BrokerDiagTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
        BrokerDiagnostics.LogDirectory = _temporaryRoot;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.LogDirectory = _originalLogDirectory;
        BrokerDiagnostics.ResetToDefaults();
        // Best-effort cleanup only: a locked file or already-missing directory must
        // not fail the test.
        try { Directory.Delete(_temporaryRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    public void Log_WhenEnabled_AppendsTimestampedLine()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        BrokerDiagnostics.Log("cold-scan-broker-ok");

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(File.ReadAllText(path), "cold-scan-broker-ok");
    }

    [TestMethod]
    public void Log_WhenDisabled_WritesNothing()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.Log("should-not-appear");

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Enable_ForcesLoggingRegardlessOfEnvVar_AndTagsRoleInLogLine()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.Enable("broker");
        BrokerDiagnostics.Log("forced-on");

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(File.ReadAllText(path), "[broker:");
    }

    [TestMethod]
    public void Log_WhenAppendFails_SwallowsExceptionAndDoesNotThrow()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        // File.AppendAllText does not create missing directories, so pointing
        // LogDirectory at one that was never created makes the write throw.
        BrokerDiagnostics.LogDirectory = Path.Combine(_temporaryRoot, "missing-subdir");

        BrokerDiagnostics.Log("should-not-throw");
    }

    [TestMethod]
    public void LogFrame_WhenEnabled_AppendsFrameTraceLine()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        BrokerDiagnostics.LogFrame("read", kind: 6, length: 42);

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsTrue(File.Exists(path));
        var line = File.ReadAllText(path);
        StringAssert.Contains(line, "frame read kind=6 len=42 t=");
    }
}
