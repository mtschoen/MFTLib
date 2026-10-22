using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The PowerShell script file a Defender exclusion run hands its (possibly elevated) child: the path the child
///     gets must keep naming the verified file however the temporary directory's junctions are retargeted, and the
///     cleanup must delete that file and nothing else. Real junctions and no elevation; only the one test that shows
///     why bracketed paths are refused starts a real (unelevated) Windows PowerShell.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DefenderExclusionScriptPinningTests
{
    const string Script = "Add-MpPreference -ExclusionProcess 'app.exe' -ErrorAction Stop";
    static readonly byte[] DecoyBytes = Encoding.UTF8.GetBytes("Write-Output 'not the verified script'");

    static byte[] ExpectedBytes()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(Script)];
    }

    static DefenderExclusionSequence SequenceOver(
        string temporaryDirectory, Func<ProcessStartInfo, Process?> startProcess, Action<string>? scriptWritten = null,
        Func<SafeFileHandle, SafeFileHandle>? takeScriptAnchor = null)
    {
        return new DefenderExclusionSequence(new ElevationSeams
        {
            StartProcess = startProcess,
            WaitForExit = (_, _) => true,
            KillProcess = _ => { },
            GetExitCode = _ => 0,
            GetTemporaryDirectory = () => temporaryDirectory,
            ScriptWritten = scriptWritten,
            TakeScriptAnchor = takeScriptAnchor
        });
    }

    static string CreateOwnedDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFTLib.DefenderExclusionScriptPinningTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // mklink /J needs no privilege, unlike a symbolic link, which is what an unprivileged attacker would use too.
    static void CreateJunction(string junction, string target)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", junction, target })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, output);
    }

    // Removing a junction removes only the link; the directory it pointed at and the files in it stay.
    static void RetargetJunction(string junction, string target)
    {
        Directory.Delete(junction);
        CreateJunction(junction, target);
    }

    // The junction goes first and on its own, so the recursive delete never walks through it.
    static void DeleteRootWithJunction(string root)
    {
        var junction = Path.Combine(root, "temp");
        if (Directory.Exists(junction))
        {
            Directory.Delete(junction);
        }

        Directory.Delete(root, recursive: true);
    }

    static byte[] ReadAsTheChildWould(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [TestMethod]
    public void RunPowerShell_JunctionRetargetedAfterTheScriptIsLocked_ChildStillReadsTheVerifiedScript()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Junctions are a Windows file system feature.");
        }

        var root = CreateOwnedDirectory();
        try
        {
            var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
            var decoy = Directory.CreateDirectory(Path.Combine(root, "decoy")).FullName;
            var junction = Path.Combine(root, "temp");
            CreateJunction(junction, staging);
            string? launched = null;
            byte[]? childReads = null;
            var sequence = SequenceOver(junction, startInfo =>
            {
                // The attacker's move between verification and the child opening its -File path.
                launched = startInfo.ArgumentList[^1];
                var name = Path.GetFileName(launched);
                RetargetJunction(junction, decoy);
                File.WriteAllBytes(Path.Combine(decoy, name), DecoyBytes);
                childReads = ReadAsTheChildWould(launched);
                return new Process();
            });

            Assert.IsTrue(sequence.RunPowerShell(Script, elevated: true));

            Assert.IsNotNull(launched);
            Assert.AreSequenceEqual(ExpectedBytes(), childReads!, "The child must read the verified script, not the decoy.");
            var decoyFile = Path.Combine(decoy, Path.GetFileName(launched));
            Assert.IsTrue(File.Exists(decoyFile), "The cleanup must not follow the retargeted junction to another file.");
            Assert.IsEmpty(Directory.GetFileSystemEntries(staging), "The cleanup must delete the verified script.");
        }
        finally
        {
            DeleteRootWithJunction(root);
        }
    }

    [TestMethod]
    public void RunPowerShell_JunctionRetargetedBeforeTheScriptIsLocked_RefusesWithoutStarting()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Junctions are a Windows file system feature.");
        }

        var root = CreateOwnedDirectory();
        try
        {
            var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
            var decoy = Directory.CreateDirectory(Path.Combine(root, "decoy")).FullName;
            var junction = Path.Combine(root, "temp");
            CreateJunction(junction, staging);
            var started = false;
            string? decoyFile = null;
            var sequence = SequenceOver(
                junction,
                _ =>
                {
                    started = true;
                    return new Process();
                },
                scriptWritten: path =>
                {
                    // A same-bytes copy defeats a content check alone; only the file identity tells them apart.
                    decoyFile = Path.Combine(decoy, Path.GetFileName(path));
                    File.WriteAllBytes(decoyFile, ExpectedBytes());
                    RetargetJunction(junction, decoy);
                });

            Assert.IsFalse(sequence.RunPowerShell(Script, elevated: true));

            Assert.IsFalse(started);
            Assert.IsTrue(File.Exists(decoyFile), "The cleanup must not delete the decoy.");
            Assert.IsEmpty(Directory.GetFileSystemEntries(staging), "The written script is still deleted.");
        }
        finally
        {
            DeleteRootWithJunction(root);
        }
    }

    [TestMethod]
    public void RunPowerShell_DirectoryThatWin32NormalizationRenames_RefusesWithoutStarting()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Win32 path normalization is Windows behavior.");
        }

        var root = CreateOwnedDirectory();
        // "trailing." is a real NTFS name, but a Win32 path through it drops the dot and names "trailing" instead.
        var trailing = @"\\?\" + Path.Combine(root, "trailing.");
        Directory.CreateDirectory(trailing);
        try
        {
            var started = false;
            var sequence = SequenceOver(trailing, _ =>
            {
                started = true;
                return new Process();
            });

            Assert.IsFalse(sequence.RunPowerShell(Script, elevated: true));

            Assert.IsFalse(started);
            Assert.IsEmpty(Directory.GetFileSystemEntries(trailing), "The written script is still deleted.");
        }
        finally
        {
            Directory.Delete(trailing, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RunPowerShell_DirectoryWithAPowerShellWildcard_RefusesWithoutStarting()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The launch path is pinned on Windows only.");
        }

        var root = CreateOwnedDirectory();
        try
        {
            // powershell.exe -File would read "dir[ab]" as a pattern and run "dira\<name>.ps1" instead.
            var bracketed = Directory.CreateDirectory(Path.Combine(root, "dir[ab]")).FullName;
            var started = false;
            var sequence = SequenceOver(bracketed, _ =>
            {
                started = true;
                return new Process();
            });

            Assert.IsFalse(sequence.RunPowerShell(Script, elevated: true));

            Assert.IsFalse(started);
            Assert.IsEmpty(Directory.GetFileSystemEntries(bracketed), "The written script is still deleted.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Why the test above matters: what Windows PowerShell 5.1 itself runs for a bracketed -File path. Win32 opens the
    // literal file; PowerShell runs the sibling the brackets match.
    [TestMethod]
    public void WindowsPowerShellFileParameter_ReadsBracketsAsAWildcardAndRunsTheSibling()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(DefenderExclusionSequence.PowerShellPath))
        {
            Assert.Inconclusive("Needs Windows PowerShell.");
        }

        var root = CreateOwnedDirectory();
        try
        {
            var literal = Directory.CreateDirectory(Path.Combine(root, "dir[ab]")).FullName;
            var sibling = Directory.CreateDirectory(Path.Combine(root, "dira")).FullName;
            File.WriteAllText(Path.Combine(literal, "script.ps1"), "Write-Output 'literal'");
            File.WriteAllText(Path.Combine(sibling, "script.ps1"), "Write-Output 'sibling'");
            var startInfo = new ProcessStartInfo(DefenderExclusionSequence.PowerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                         Path.Combine(literal, "script.ps1")
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            Assert.AreEqual("sibling", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RunPowerShell_AnchorCannotBeTaken_RefusesAndNeverDeletesByPathname()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Junctions are a Windows file system feature.");
        }

        var root = CreateOwnedDirectory();
        try
        {
            var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
            var decoy = Directory.CreateDirectory(Path.Combine(root, "decoy")).FullName;
            var junction = Path.Combine(root, "temp");
            CreateJunction(junction, staging);
            var started = false;
            string? decoyFile = null;
            var sequence = SequenceOver(
                junction,
                _ =>
                {
                    started = true;
                    return new Process();
                },
                takeScriptAnchor: _ =>
                {
                    // Without an anchor the only way left to delete is by pathname, which now names the decoy.
                    var name = Directory.GetFiles(staging).Single();
                    decoyFile = Path.Combine(decoy, Path.GetFileName(name));
                    File.WriteAllBytes(decoyFile, DecoyBytes);
                    RetargetJunction(junction, decoy);
                    throw new IOException("The anchor could not be taken.");
                });

            Assert.IsFalse(sequence.RunPowerShell(Script, elevated: true));

            Assert.IsFalse(started);
            Assert.IsTrue(File.Exists(decoyFile), "A failed anchor must not turn into a delete by pathname.");
        }
        finally
        {
            DeleteRootWithJunction(root);
        }
    }

    [TestMethod]
    public void Dispose_CalledTwice_DoesNothingTheSecondTime()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            var held = HeldScriptFile.Create(directory, Script);
            held.Dispose();
            held.Dispose();

            Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(@"\\?\D:\Scratch\Temp\a.ps1", @"D:\Scratch\Temp\a.ps1")]
    [DataRow(@"\\?\c:\a.ps1", @"c:\a.ps1")]
    public void ToLocalDrivePath_DropsTheLongPathPrefixPowerShellRefuses(string finalPath, string expected)
    {
        Assert.AreEqual(expected, HeldScriptFile.ToLocalDrivePath(finalPath));
    }

    [TestMethod]
    [DataRow(@"\\?\UNC\server\share\Temp\a.ps1")]
    [DataRow(@"\\?\unc\server\share\a.ps1")]
    [DataRow(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\a.ps1")]
    [DataRow(@"\\?\1:\a.ps1")]
    [DataRow(@"\\?\D:")]
    [DataRow(@"D:\already\plain.ps1")]
    [DataRow(@"\\server\share\a.ps1")]
    public void ToLocalDrivePath_AnythingButALocalDrive_IsRefused(string finalPath)
    {
        Assert.ThrowsExactly<IOException>(() => HeldScriptFile.ToLocalDrivePath(finalPath));
    }

    [TestMethod]
    [DataRow(@"D:\Scratch\dir[ab]\a.ps1")]
    [DataRow(@"D:\Scratch\dir]\a.ps1")]
    [DataRow(@"D:\Scratch\tick`b\a.ps1")]
    public void RequireSameMeaningToPowerShell_WildcardOrEscapeCharacter_IsRefused(string launchPath)
    {
        Assert.ThrowsExactly<IOException>(() => HeldScriptFile.RequireSameMeaningToPowerShell(launchPath));
    }

    [TestMethod]
    [DataRow(@"D:\", true)]
    [DataRow(@"z:\", true)]
    [DataRow(@"D:\Mounts\Volume\", false)]
    [DataRow(@"\\server\share\", false)]
    [DataRow(@"D:", false)]
    public void IsDriveRoot_OnlyABareDriveRoot(string volumePath, bool expected)
    {
        Assert.AreEqual(expected, HeldScriptFile.IsDriveRoot(volumePath));
    }
}
