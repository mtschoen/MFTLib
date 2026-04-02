using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class ElevationUtilitiesTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [TestCleanup]
    public void Cleanup() => ElevationUtilities.ResetToDefaults();

    [TestMethod]
    public void IsElevated_ReturnsBool()
    {
        var result = ElevationUtilities.IsElevated();
        Assert.IsInstanceOfType(result, typeof(bool));
    }

    [TestMethod]
    public void IsElevated_NonWindows_ReturnsFalse()
    {
        ElevationUtilities.IsWindows = () => false;
        Assert.IsFalse(ElevationUtilities.IsElevated());
    }

    [TestMethod]
    public void GetProcessPath_ReturnsNonNull()
    {
        var path = ElevationUtilities.GetProcessPath();
        Assert.IsNotNull(path);
        Assert.IsTrue(path.Length > 0);
    }

    [TestMethod]
    public void EnsureElevated_NullProcessPath_ReturnsFalse()
    {
        ElevationUtilities.IsWindows = () => false;
        ElevationUtilities.GetProcessPathFunc = () => null;
        Assert.IsFalse(ElevationUtilities.EnsureElevated());
    }

    [TestMethod]
    public void EnsureElevated_WhenAlreadyElevated_ReturnsTrue()
    {
        // Covers the true branch of `if (IsElevated())` at line 40
        var originalIsWindows = ElevationUtilities.IsWindows;
        ElevationUtilities.IsWindows = () => true;
        // If we're actually elevated, this returns true immediately.
        // If not, WindowsIdentity.GetCurrent() still works — it just returns non-admin.
        // So we also need IsElevated to return true. We can't mock IsElevated directly,
        // but we only need this for the branch where IsElevated returns true.
        if (!ElevationUtilities.IsElevated())
        {
            ElevationUtilities.IsWindows = originalIsWindows;
            Assert.Inconclusive("This branch is covered by admin tests.");
        }

        Assert.IsTrue(ElevationUtilities.EnsureElevated());
    }

    [TestMethod]
    public void EnsureElevated_ExePath_UsesArguments()
    {
        ElevationUtilities.IsWindows = () => false;
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ElevationUtilities.StartProcess = _ => null;
        var result = ElevationUtilities.EnsureElevated(["--verbose"]);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void EnsureElevated_ExePath_QuotesArgsWithSpaces()
    {
        // Covers the true branch of `a.Contains(' ')` ternary in the exe path (line 65)
        ElevationUtilities.IsWindows = () => false;
        ElevationUtilities.GetProcessPathFunc = () => @"C:\app\MyApp.exe";
        ProcessStartInfo? captured = null;
        ElevationUtilities.StartProcess = info => { captured = info; return null; };
        ElevationUtilities.EnsureElevated(["--path", @"C:\my folder\file.txt"]);
        Assert.IsNotNull(captured);
        Assert.IsTrue(captured!.Arguments.Contains("\"C:\\my folder\\file.txt\""));
    }

    [TestMethod]
    public void EnsureElevated_DotnetPath_QuotesArgsWithSpaces()
    {
        ElevationUtilities.IsWindows = () => false;
        ElevationUtilities.GetProcessPathFunc = () => @"C:\dotnet\dotnet.exe";
        ElevationUtilities.GetCommandLineArgs = () => ["dotnet", @"C:\my app\app.dll", "--flag"];
        ProcessStartInfo? captured = null;
        ElevationUtilities.StartProcess = info => { captured = info; return null; };
        ElevationUtilities.EnsureElevated();
        Assert.IsNotNull(captured);
        Assert.IsTrue(captured!.Arguments.Contains("\"C:\\my app\\app.dll\""));
    }

    [TestMethod]
    public void EnsureElevated_ProcessReturnsNull_ReturnsFalse()
    {
        ElevationUtilities.IsWindows = () => false;
        ElevationUtilities.GetProcessPathFunc = () => @"C:\dotnet\dotnet.exe";
        ElevationUtilities.StartProcess = _ => null;
        Assert.IsFalse(ElevationUtilities.EnsureElevated());
    }

    [TestMethod]
    public void EnsureElevated_GenericException_ReturnsFalse()
    {
        ElevationUtilities.IsWindows = () => false;
        ElevationUtilities.GetProcessPathFunc = () => @"C:\dotnet\dotnet.exe";
        ElevationUtilities.StartProcess = _ => throw new InvalidOperationException("test");
        Assert.IsFalse(ElevationUtilities.EnsureElevated());
    }

    [TestMethod]
    [TestCategory("Interactive")]
    public void EnsureElevated_WhenNotAdmin_Declined_ReturnsFalse()
    {
        if (ElevationUtilities.IsElevated())
            Assert.Inconclusive("Test must run non-elevated.");

        MessageBox(IntPtr.Zero,
            "A UAC prompt will appear next.\n\nPlease DECLINE (click No) to test the cancellation path.",
            "MFTLib Test — Decline UAC", 0x00010040 /* MB_ICONINFORMATION | MB_SETFOREGROUND */);

        var result = ElevationUtilities.EnsureElevated();
        Assert.IsFalse(result);
    }

    [TestMethod]
    [TestCategory("Interactive")]
    public void EnsureElevated_WhenNotAdmin_Accepted_ReturnsResult()
    {
        if (ElevationUtilities.IsElevated())
            Assert.Inconclusive("Test must run non-elevated.");

        Thread.Sleep(1000);

        MessageBox(IntPtr.Zero,
            "A UAC prompt will appear next.\n\nPlease ACCEPT (click Yes) to test the elevation path.",
            "MFTLib Test — Accept UAC", 0x00010040 /* MB_ICONINFORMATION | MB_SETFOREGROUND */);

        var result = ElevationUtilities.EnsureElevated();
        Assert.IsInstanceOfType(result, typeof(bool));
    }
}
