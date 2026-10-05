using System.Diagnostics;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The elevation guard is active for the whole test process (the module initializer), so these tests drive the
// real default start path against it and the decision function with both outcomes. Nothing here may start a process.
[TestClass]
[DoNotParallelize]
public class ElevationIsolationTests
{
    [TestCleanup]
    public void Cleanup()
    {
        ElevationUtilities.ResetToDefaults();
        BrokerLauncher.ResetToDefaults();
    }

    [TestMethod]
    public void ForbiddenStart_ThrowsNamingTheExecutableAndArgumentsAndStartsNothing()
    {
        var started = false;
        var startInfo = new ProcessStartInfo(@"C:\app\MyApp.exe", "--broker --pipe p") { Verb = "runas" };

        var exception = Assert.ThrowsException<ElevationForbiddenException>(
            () => ElevationGuard.StartUnlessForbidden(true, _ =>
            {
                started = true;
                return null;
            }, startInfo));

        Assert.IsFalse(started);
        StringAssert.Contains(exception.Message, @"C:\app\MyApp.exe");
        StringAssert.Contains(exception.Message, "runas");
        StringAssert.Contains(exception.Message, "--broker --pipe p");
        Assert.IsInstanceOfType<InvalidOperationException>(exception);
    }

    [TestMethod]
    public void ForbiddenStart_NamesArgumentListEntriesWhenThereIsNoArgumentString()
    {
        var startInfo = new ProcessStartInfo(@"C:\app\MyApp.exe") { Verb = "runas" };
        startInfo.ArgumentList.Add("find-name");
        startInfo.ArgumentList.Add("C");

        var exception = Assert.ThrowsException<ElevationForbiddenException>(
            () => ElevationGuard.StartUnlessForbidden(true, _ => null, startInfo));

        StringAssert.Contains(exception.Message, "find-name C");
    }

    [TestMethod]
    public void PermittedStart_RunsTheSuppliedStart()
    {
        var startInfo = new ProcessStartInfo("x");
        ProcessStartInfo? seen = null;

        var result = ElevationGuard.StartUnlessForbidden(false, info =>
        {
            seen = info;
            return null;
        }, startInfo);

        Assert.IsNull(result);
        Assert.AreSame(startInfo, seen);
    }

    [TestMethod]
    public void ForbiddenException_KeepsItsMessageAndInnerException()
    {
        var inner = new InvalidOperationException("inner");

        Assert.AreEqual("m", new ElevationForbiddenException("m").Message);
        Assert.AreSame(inner, new ElevationForbiddenException("m", inner).InnerException);
        Assert.IsNotNull(new ElevationForbiddenException().Message);
    }

    [TestMethod]
    public void ForbidElevation_IsIdempotent()
    {
        ElevationIsolation.ForbidElevation();
        ElevationIsolation.ForbidElevation();

        Assert.ThrowsException<ElevationForbiddenException>(() => ElevationGuard.Start(new ProcessStartInfo("x")));
    }

    [TestMethod]
    public void TryRunElevated_WithTheDefaultStart_ThrowsInsteadOfPromptingEvenAfterResetToDefaults()
    {
        // The shape of a test that reaches the relaunch with default dependencies.
        ElevationUtilities.ResetToDefaults();
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;

        var exception = Assert.ThrowsException<ElevationForbiddenException>(
            () => ElevationUtilities.TryRunElevated(["find-name", "C"], ElevationUtilities.DefaultElevatedTimeout));

        StringAssert.Contains(exception.Message, @"C:\app\MyApp.exe");
    }

    [TestMethod]
    public void TryRunElevated_AStubbedStartIsUnaffected()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => null;

        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    public void TryRunElevated_AnOrdinaryStartFailureStillReportsFalse()
    {
        ElevationUtilities._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities._isUserInteractive = () => true;
        ElevationUtilities._startProcess = _ => throw new InvalidOperationException("start failed");

        Assert.IsFalse(ElevationUtilities.TryRunElevated(["--test"], ElevationUtilities.DefaultElevatedTimeout));
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void BrokerLauncher_WithTheDefaultStart_ThrowsInsteadOfPrompting()
    {
        BrokerLauncher.ResetToDefaults();
        BrokerLauncher._getProcessPathFunc = () => @"C:\app\MyApp.exe";

        var exception = Assert.ThrowsException<ElevationForbiddenException>(
            () => BrokerLauncher.Launch("--broker --pipe p"));

        StringAssert.Contains(exception.Message, "--broker --pipe p");
    }
}
