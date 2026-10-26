using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The real, unelevated <c>FSCTL_QUERY_USN_JOURNAL</c> path in
///     <see cref="UsnJournalSettingsQuery" />: no test category, so this runs in the
///     standard non-admin pass (fsutil usn queryjournal works unelevated, and so must
///     this). The marshaling success path is covered here on Windows; the
///     platform guard is covered on Linux.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UsnJournalSettingsQueryLiveTests
{
    [TestCleanup]
    public void Cleanup() => UsnJournalSettingsQuery.Dependencies.ResetToDefaults();

    [TestMethod]
    public void Query_HostIsNotWindows_ThrowsPlatformNotSupported()
    {
        var exception = Assert.ThrowsExactly<PlatformNotSupportedException>(
            () => UsnJournalSettingsQuery.Query('C', isWindows: false));

        Assert.Contains("FSCTL_QUERY_USN_JOURNAL", exception.Message);
    }

    /// <summary>
    ///     The platform-taking query reads nothing but its arguments: an installed override
    ///     answers the public entry point and is never consulted by the platform-taking one.
    /// </summary>
    [TestMethod]
    public void Query_OverrideInstalled_AnswersThePublicEntryAndNotThePlatformTakingQuery()
    {
        var overridden = new UsnJournalSettings { MaximumSize = 7, AllocationDelta = 3 };
        UsnJournalSettingsQuery.Dependencies._queryOverride = _ => overridden;

        Assert.AreEqual(overridden, UsnJournalSettingsQuery.Query('C'));
        Assert.ThrowsExactly<PlatformNotSupportedException>(
            () => UsnJournalSettingsQuery.Query('C', isWindows: false));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Query_OnRealVolume_ReturnsSettingsUnelevated()
    {
        var settings = UsnJournalSettingsQuery.Query('C');

        Assert.IsGreaterThan(0, settings.MaximumSize, "MaximumSize should be positive");
        Assert.IsGreaterThan(0, settings.AllocationDelta, "AllocationDelta should be positive");
        Assert.IsGreaterThanOrEqualTo(settings.AllocationDelta, settings.MaximumSize,
            "The maximum should be at least one allocation delta");
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void Query_NonWindowsHost_ThrowsPlatformNotSupported()
    {
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => UsnJournalSettingsQuery.Query('C'));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Query_ForADriveLetterThatDoesNotExist_ThrowsIOExceptionNamingTheRoot()
    {
        var missing = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(value => (char)value)
            .FirstOrDefault(letter => !Directory.Exists($"{letter}:\\"));
        if (missing == '\0')
        {
            Assert.Inconclusive("Every drive letter D-Z exists on this machine");
            return;
        }

        var exception = Assert.ThrowsExactly<IOException>(() => UsnJournalSettingsQuery.Query(missing));

        Assert.Contains($"{missing}:\\", exception.Message);
    }
}
