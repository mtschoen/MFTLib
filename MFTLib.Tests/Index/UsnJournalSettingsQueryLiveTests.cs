using System.Runtime.Versioning;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The real, unelevated <c>FSCTL_QUERY_USN_JOURNAL</c> path in
///     <see cref="UsnJournalSettingsQuery" />: no test category, so this runs in the
///     standard non-admin pass (fsutil usn queryjournal works unelevated, and so must
///     this). The marshaling success path is covered here on Windows; the
///     platform guard is covered on Linux. The
///     <see cref="SupportedOSPlatformAttribute" /> on each test mirrors
///     <c>IndexedDriveTests</c>: it satisfies CA1416 and is inert at runtime.
/// </summary>
[TestClass]
public class UsnJournalSettingsQueryLiveTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Query_OnRealVolume_ReturnsSettingsUnelevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires a Windows NTFS volume");
            return;
        }

        var settings = UsnJournalSettingsQuery.Query('C');

        Assert.IsTrue(settings.MaximumSize > 0, "MaximumSize should be positive");
        Assert.IsTrue(settings.AllocationDelta > 0, "AllocationDelta should be positive");
        Assert.IsTrue(settings.MaximumSize >= settings.AllocationDelta,
            "The maximum should be at least one allocation delta");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Query_NonWindowsHost_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Covers the non-Windows guard only");
            return;
        }

        Assert.ThrowsException<PlatformNotSupportedException>(() => UsnJournalSettingsQuery.Query('C'));
    }
}
