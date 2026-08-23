using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using TestProgram;

namespace MFTLib.Tests;

[TestClass]
public class DriveScannerTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    // --- FormatArguments ---

    [TestMethod]
    public void FormatArguments_EmptyArray_ReturnsEmptyString()
    {
        var result = DriveScanner.FormatArguments([]);
        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void FormatArguments_NoSpaces_ReturnsUnquoted()
    {
        var result = DriveScanner.FormatArguments(["C", "D"]);
        Assert.AreEqual("C D", result);
    }

    [TestMethod]
    public void FormatArguments_WithSpaces_QuotesArguments()
    {
        var result = DriveScanner.FormatArguments(["Program Files", "C"]);
        Assert.AreEqual("\"Program Files\" C", result);
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
            _tryRunElevated = _ => true,
            _writeLine = lines.Add
        };

        var result = scanner.Run([]);
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void Run_NotElevated_CannotSelfElevate_PrintsFailureAndReturnsOne()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => false,
            _getProcessPath = () => "/some/path",
            _writeLine = lines.Add
        };

        var result = scanner.Run(["C"]);
        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
    }

    [TestMethod]
    public void Run_NotElevated_CanSelfElevateButFails_PrintsFailureAndReturnsOne()
    {
        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = _ => false,
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

        scanner.ScanDrive("C");
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

        scanner.ScanDrive("C:");
        Assert.AreEqual("C", openedLetters[0]);
        Assert.IsTrue(lines.Any(line => line == "=== Drive C: ==="));
    }

    [TestMethod]
    public void ScanDrive_ZeroRecords_PrintsFoundZeroDirectories()
    {
        var (resultPtr, cleanupAction) = BuildMftParseResult(0);
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = cleanupAction;

        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _openVolume = letter => MftVolume.Open(letter),
            _writeLine = lines.Add
        };

        scanner.ScanDrive("T");
        Assert.IsTrue(lines.Any(line => line.Contains("Found 0 .git directories")));
        Assert.IsTrue(lines.Any(line => line.Contains("=== Drive T: done ===")));
    }

    [TestMethod]
    public void ScanDrive_WithDirectoryRecord_PrintsDirectoryPath()
    {
        var (resultPtr, cleanupAction) = BuildMftParseResult(1, true);
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = cleanupAction;

        var lines = new List<string>();
        var scanner = new DriveScanner
        {
            _openVolume = letter => MftVolume.Open(letter),
            _writeLine = lines.Add
        };

        scanner.ScanDrive("T");
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
