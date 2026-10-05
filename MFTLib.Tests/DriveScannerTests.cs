using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using TestProgram;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class DriveScannerTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    // --- Run: elevation paths ---

    [TestMethod]
    public void Run_NotElevated_SelfElevateSucceeds_ReturnsZero()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (_, _) => true,
            _writeLine = lines.Add
        };

        var result = scanner.Run([]);
        Assert.AreEqual(0, result);
    }

    [DataTestMethod]
    [DataRow(new[] { "C" }, DisplayName = "default-drive")]
    [DataRow(new[] { "find-name", "C", "--name", "", "--include-freed" }, DisplayName = "empty-name")]
    [DataRow(new[] { "find-name", "C", "--name", "x&echo(123", "--include-freed" }, DisplayName = "cmd-ampersand")]
    [DataRow(new[] { "find-name", "C", "--name", "x;echo(123);#", "--include-freed" }, DisplayName = "powershell-separator")]
    [DataRow(new[] { "find-name", "C", "--name", "$(Get-Date)", "--include-freed" }, DisplayName = "powershell-subexpression")]
    [DataRow(new[] { "find-name", "C", "--name", "%USERNAME%", "--include-freed" }, DisplayName = "cmd-variable")]
    [DataRow(new[] { "find-name", "C", "--name", "$env:USERNAME", "--include-freed" }, DisplayName = "powershell-variable")]
    [DataRow(new[] { "find-name", "C", "--name", "`n", "--include-freed" }, DisplayName = "backtick-n")]
    [DataRow(new[] { "find-name", "C", "--name", "a'b", "--include-freed" }, DisplayName = "single-quote")]
    [DataRow(new[] { "find-name", "C", "--name", "say\"hi", "--include-freed" }, DisplayName = "embedded-quote")]
    [DataRow(new[] { "find-name", "C", "--name", @"C:\spaced directory\", "--include-freed" }, DisplayName = "trailing-backslash")]
    public void Run_NotElevated_CannotSelfElevate_PrintsEachArgumentVerbatimOnItsOwnLine(string[] arguments)
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => false,
            _getProcessPath = () => @"C:pp\TestProgram.exe",
            _writeLine = lines.Add
        };

        var result = scanner.Run(arguments);
        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
        Assert.IsTrue(lines.Any(line => line.Contains(@"C:pp\TestProgram.exe")));

        var headerIndex = lines.FindIndex(line => line.StartsWith($"Arguments ({arguments.Length})", StringComparison.Ordinal));
        Assert.IsTrue(headerIndex >= 0, "argument count header missing");
        var listed = lines.Skip(headerIndex + 1).Take(arguments.Length).ToArray();
        var expected = arguments.Select(argument => argument.Length == 0 ? "  <empty>" : "  " + argument).ToArray();
        CollectionAssert.AreEqual(expected, listed);
        Assert.AreEqual(headerIndex + arguments.Length + 2, lines.Count, "only the closing rule may follow the arguments");
    }

    [TestMethod]
    public void Run_NotElevated_CanSelfElevateButFails_PrintsFailureAndReturnsOne()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (_, _) => false,
            _getProcessPath = () => "/some/path",
            _writeLine = lines.Add
        };

        var result = scanner.Run(["C"]);
        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
    }

    // --- Run: elevated paths ---

    [TestMethod]
    public void Run_Elevated_NoArgs_UsesDefaultDriveG()
    {
        var scannedDrives = new List<string>();
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
            _openVolume = letter =>
            {
                scannedDrives.Add(letter);
                throw new IOException("Mock: drive not available");
            },
            _writeLine = lines.Add
        };

        scanner.Run([]);
        Assert.IsTrue(scannedDrives.Contains("G"));
    }

    [TestMethod]
    public void Run_Elevated_WithArgs_ScansSpecifiedDrives()
    {
        var scannedDrives = new List<string>();
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
            _openVolume = letter =>
            {
                scannedDrives.Add(letter);
                throw new IOException("Mock: drive not available");
            },
            _writeLine = lines.Add
        };

        scanner.Run(["C", "D"]);
        CollectionAssert.Contains(scannedDrives, "C");
        CollectionAssert.Contains(scannedDrives, "D");
        Assert.AreEqual(2, scannedDrives.Count);
    }

    [TestMethod]
    public void Run_Elevated_RedirectsStdout()
    {
        uint capturedIndex = 0;
        string? redirectedPath = null;
        var scanner = new DriveScanner
        {
            _isElevated = () => true,
            _acrtIobFunc = index =>
            {
                capturedIndex = index;
                return new IntPtr(42);
            },
            _wFreopen = (path, _, _) =>
            {
                redirectedPath = path;
                return IntPtr.Zero;
            },
            _openVolume = _ => throw new IOException("Mock"),
            _writeLine = _ => { }
        };

        scanner.Run(["T"]);
        Assert.AreEqual(1u, capturedIndex);
        Assert.IsNotNull(redirectedPath);
        Assert.IsTrue(redirectedPath!.EndsWith("output.log", StringComparison.Ordinal));
    }

    // --- ScanDrive ---

    [TestMethod]
    public void ScanDrive_VolumeOpenError_PrintsErrorMessage()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _openVolume = _ => throw new IOException("Access denied"),
            _writeLine = lines.Add
        };

        scanner.ScanDrive("C", new ModeOptions());
        Assert.IsTrue(lines.Any(line => line.Contains("Error on drive C")));
        Assert.IsTrue(lines.Any(line => line.Contains("Access denied")));
    }

    [TestMethod]
    public void ScanDrive_StripsTrailingColon()
    {
        var openedLetters = new List<string>();
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _openVolume = letter =>
            {
                openedLetters.Add(letter);
                throw new IOException("Mock");
            },
            _writeLine = lines.Add
        };

        scanner.ScanDrive("C:", new ModeOptions());
        Assert.AreEqual("C", openedLetters[0]);
        Assert.IsTrue(lines.Any(line => line == "=== Drive C: ==="));
    }

    [TestMethod]
    public void ScanDrive_ZeroRecords_PrintsFoundZeroDirectories()
    {
        var (resultPtr, cleanupAction) = BuildMftParseResult(0);
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = cleanupAction;

        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _openVolume = letter => MftVolume.Open(letter),
            _writeLine = lines.Add
        };

        scanner.ScanDrive("T", new ModeOptions());
        Assert.IsTrue(lines.Any(line => line.Contains("Found 0 .git directories")));
        Assert.IsTrue(lines.Any(line => line.Contains("=== Drive T: done ===")));
    }

    [TestMethod]
    public void ScanDrive_WithDirectoryRecord_PrintsDirectoryPath()
    {
        var (resultPtr, cleanupAction) = BuildMftParseResult(1, true);
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = cleanupAction;

        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _openVolume = letter => MftVolume.Open(letter),
            _writeLine = lines.Add
        };

        scanner.ScanDrive("T", new ModeOptions());
        Assert.IsTrue(lines.Any(line => line.Contains("Found 1 .git directories")));
        Assert.IsTrue(lines.Any(line => line.Contains("=== Drive T: done ===")));
    }

    // --- Entry point ---

    [TestMethod]
    public void TestProgram_EntryPoint_RunsAndExits()
    {
        var entryPoint = typeof(DriveScanner).Assembly.EntryPoint!;
        var exitCode = entryPoint.Invoke(null, [Array.Empty<string>()]);
        // Non-elevated: prints failure message and returns 1
        // Elevated: scans default drive G and returns 0
        Assert.IsTrue(exitCode is 0 or 1);
    }

    // --- Helpers ---

    static unsafe (IntPtr resultPtr, Action<IntPtr> cleanup) BuildMftParseResult(ulong recordCount,
        bool includeDirectory = false)
    {
        var entrySize = (int)MFTLibNative.NativeCompactEntrySize;

        var entriesPtr = IntPtr.Zero;
        var stringsPtr = IntPtr.Zero;
        var stringUnits = 0UL;
        if (recordCount > 0)
        {
            var bufferSize = (int)recordCount * entrySize;
            entriesPtr = Marshal.AllocHGlobal(bufferSize);
            new Span<byte>((void*)entriesPtr, bufferSize).Clear();

            if (includeDirectory)
            {
                var path = ".git";
                stringUnits = (ulong)path.Length;
                stringsPtr = Marshal.AllocHGlobal(path.Length * sizeof(char));
                path.AsSpan().CopyTo(new Span<char>((void*)stringsPtr, path.Length));

                var entryPtr = (byte*)entriesPtr;
                *(ulong*)entryPtr = 1UL;
                *(ulong*)(entryPtr + 8) = 5UL;
                *(ulong*)(entryPtr + 16) = 0UL; // stringOffset
                *(uint*)(entryPtr + 24) = (uint)FileAttributes.Directory;
                *(ushort*)(entryPtr + 28) = 0x0003; // InUse | Directory
                *(ushort*)(entryPtr + 30) = (ushort)path.Length;
            }
        }

        var result = new MftParseResult
        {
            TotalRecords = recordCount,
            UsedRecords = recordCount,
            PathEntries = entriesPtr,
            PathStrings = stringsPtr,
            PathStringUnits = stringUnits,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);

        var capturedEntriesPtr = entriesPtr;
        var capturedStringsPtr = stringsPtr;

        void CleanupAllocations(IntPtr pointer)
        {
            if (capturedEntriesPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(capturedEntriesPtr);
            }

            if (capturedStringsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(capturedStringsPtr);
            }

            Marshal.FreeHGlobal(pointer);
        }

        return (resultPtr, CleanupAllocations);
    }
}
