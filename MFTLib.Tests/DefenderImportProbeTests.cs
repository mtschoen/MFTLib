using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The import line a Defender exclusion script starts with, run by a real unelevated Windows PowerShell 5.1 (the
///     shell the elevated launch uses) while a fake <c>Defender</c> module sits first on <c>PSModulePath</c>. The
///     probe only imports the module and reads where it was loaded from: it never calls <c>Add-MpPreference</c> and
///     never changes an exclusion.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DefenderImportProbeTests
{
    [TestMethod]
    public void ImportLine_FakeDefenderModuleFirstOnModulePath_LoadsTheSystemModuleAndNeverRunsTheFake()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(DefenderExclusionSequence.PowerShellPath))
        {
            Assert.Inconclusive("Needs Windows PowerShell 5.1.");
        }

        var systemModuleDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "Modules", "Defender");
        if (!File.Exists(Path.Combine(systemModuleDirectory, "Defender.psd1")))
        {
            Assert.Inconclusive("This Windows installation has no system Defender module.");
        }

        var importLine = DefenderExclusionSequence.BuildScript(["probe.exe"]).Split("; ")[0];
        var root = Path.Combine(Path.GetTempPath(), "MFTLib.DefenderImportProbeTests", Guid.NewGuid().ToString("N"));
        var fakeModules = Directory.CreateDirectory(Path.Combine(root, "modules")).FullName;
        var fakeModule = Directory.CreateDirectory(Path.Combine(fakeModules, "Defender")).FullName;
        var marker = Path.Combine(root, "fake-module-ran.txt");
        try
        {
            File.WriteAllText(Path.Combine(fakeModule, "Defender.psm1"), "Set-Content -LiteralPath $env:FAKE_DEFENDER_MARKER -Value 'ran'");
            File.WriteAllText(Path.Combine(fakeModule, "Defender.psd1"), "@{ ModuleVersion = '9.9.9'; RootModule = 'Defender.psm1' }");
            var startInfo = new ProcessStartInfo(DefenderExclusionSequence.PowerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["FAKE_DEFENDER_MARKER"] = marker;
            startInfo.Environment["PSModulePath"] = fakeModules + ";" + Environment.GetEnvironmentVariable("PSModulePath");
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                         importLine + "; (Get-Module -Name Defender).ModuleBase"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            var errorTask = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            var error = errorTask.Result;

            Assert.AreEqual(0, process.ExitCode, error);
            Assert.AreEqual(systemModuleDirectory, output, ignoreCase: true);
            Assert.IsFalse(File.Exists(marker), "The planted Defender module must never execute.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
