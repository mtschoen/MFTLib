using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The PowerShell script file a Defender exclusion run hands its (possibly elevated) child: the path the child
///     gets must keep naming the verified file however the temporary directory's junctions are retargeted, and the
///     cleanup must delete that file and nothing else. Real junctions, no real PowerShell, no elevation.
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
        string temporaryDirectory, Func<ProcessStartInfo, Process?> startProcess, Action<string>? scriptWritten = null)
    {
        return new DefenderExclusionSequence(new ElevationSeams
        {
            StartProcess = startProcess,
            WaitForExit = (_, _) => true,
            KillProcess = _ => { },
            GetExitCode = _ => 0,
            GetTemporaryDirectory = () => temporaryDirectory,
            ScriptWritten = scriptWritten
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
    [DataRow(@"\\?\D:\Scratch\Temp\a.ps1", @"D:\Scratch\Temp\a.ps1")]
    [DataRow(@"\\?\UNC\server\share\Temp\a.ps1", @"\\server\share\Temp\a.ps1")]
    [DataRow(@"\\?\unc\server\share\a.ps1", @"\\server\share\a.ps1")]
    [DataRow(@"C:\already\plain.ps1", @"C:\already\plain.ps1")]
    public void ToWin32Path_DropsTheLongPathPrefixPowerShellRefuses(string finalPath, string expected)
    {
        Assert.AreEqual(expected, HeldScriptFile.ToWin32Path(finalPath));
    }
}
