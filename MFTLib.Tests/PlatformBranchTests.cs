using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Branches that select on the host platform take it as an input, so the non-Windows outcome is
// checked on every host instead of only where the branch happens to run.
[TestClass]
public class PlatformBranchTests
{
    [TestMethod]
    public void ResolveLogPath_WindowsHost_ResolvesAgainstTheCurrentDirectory()
    {
        var relative = Path.Combine("logs", "broker-diagnostics.log");

        Assert.AreEqual(Path.GetFullPath(relative), BrokerDiagnostics.ResolveLogPath(relative, isWindows: true));
    }

    [TestMethod]
    public void ResolveLogPath_NonWindowsHostWithADriveRoot_KeepsTheWindowsPathUnchanged()
    {
        const string driveRooted = @"C:\broker-diag\broker-diagnostics.log";
        var extended = string.Concat(@"\\", "?", @"\D:", @"\broker-diag\broker-diagnostics.log");

        Assert.AreEqual(driveRooted, BrokerDiagnostics.ResolveLogPath(driveRooted, isWindows: false));
        Assert.AreEqual(extended, BrokerDiagnostics.ResolveLogPath(extended, isWindows: false));
    }

    [TestMethod]
    public void ResolveLogPath_NonWindowsHostWithARelativePath_ResolvesAgainstTheCurrentDirectory()
    {
        var relative = Path.Combine("logs", "broker-diagnostics.log");

        Assert.AreEqual(Path.GetFullPath(relative), BrokerDiagnostics.ResolveLogPath(relative, isWindows: false));
    }

    [TestMethod]
    public void ResolveFileReference_NonWindowsHost_CannotResolveAndReturnsNull()
    {
        Assert.IsNull(BrokerDiagnosticsLogFilter.ResolveFileReference(
            Path.GetFullPath(Environment.ProcessPath!), isWindows: false));
    }

    [TestMethod]
    public void NormalizePath_NonWindowsHost_ReturnsThePathAsGiven()
    {
        const string path = @"logs\..\broker-diagnostics.log";

        Assert.AreEqual(path, BrokerDiagnosticsLogFilter.NormalizePath(path, isWindows: false));
        Assert.AreEqual(Path.GetFullPath(path), BrokerDiagnosticsLogFilter.NormalizePath(path, isWindows: true));
    }

    [TestMethod]
    public void NtfsVolumeInformationQuery_NonWindowsHost_ThrowsPlatformNotSupported()
    {
        var thrown = Assert.ThrowsException<PlatformNotSupportedException>(() =>
            NtfsVolumeInformation.Query("C", isWindows: false));

        StringAssert.Contains(thrown.Message, "FSCTL_GET_NTFS_VOLUME_DATA");
    }
}
