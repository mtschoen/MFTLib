using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using TestProgram;

namespace MFTLib.Tests;

// The volume-level modes with their default seams left in place, so the real library calls run
// against the Kernel32 and native seams that stand in for the volume.
[TestClass]
[DoNotParallelize]
public class DriveScannerDefaultSeamTests
{
    [TestInitialize]
    public void Initialize()
    {
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        Kernel32.ResetToDefaults();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Run_VolumeInfo_ReadsTheNtfsVolumeData()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("FSCTL_GET_NTFS_VOLUME_DATA requires Windows");
            return;
        }

        Kernel32._deviceIoControl = (_, _, _, _, outBuffer, _, out bytesReturned, _) =>
        {
            Marshal.StructureToPtr(new NtfsVolumeDataBufferNative
            {
                MftValidDataLength = 409_600,
                BytesPerFileRecordSegment = 1024
            }, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
        var lines = new List<string>();

        ElevatedScanner(lines).Run(["volume-info", "T"]);

        Assert.IsTrue(lines.Contains("MFT valid data length 409600 bytes"));
        Assert.IsTrue(lines.Contains("Approximate MFT record count 400"));
    }

    [TestMethod]
    public void Run_UsnGrow_GrowsThroughTheControlRequestAndPrintsBothReads()
    {
        var queryCount = 0;
        var settingsBefore = BuildJournalInfo(0x100000, 0x40000);
        var settingsAfter = BuildJournalInfo(0x400000, 0x200000);
        long requestedMaximum = 0;
        long requestedDelta = 0;
        try
        {
            // The mode reads the sizing, the grow reads it again before the change, then once after.
            MFTLibNative._queryUsnJournal = _ => ++queryCount <= 2 ? settingsBefore : settingsAfter;
            MFTLibNative._freeUsnJournalInfo = _ => { };
            Kernel32._deviceIoControl = (_, _, inBuffer, _, _, _, out bytesReturned, _) =>
            {
                requestedMaximum = Marshal.ReadInt64(inBuffer, 0);
                requestedDelta = Marshal.ReadInt64(inBuffer, 8);
                bytesReturned = 0;
                return true;
            };
            var lines = new List<string>();

            ElevatedScanner(lines).Run(["usn-grow", "T", "--maximum-size", "4194304", "--allocation-delta", "2097152"]);

            Assert.AreEqual(4194304L, requestedMaximum);
            Assert.AreEqual(2097152L, requestedDelta);
            Assert.IsTrue(lines.Contains("Before: maximum size 1048576 bytes, allocation delta 262144 bytes"));
            Assert.IsTrue(lines.Contains("After: maximum size 4194304 bytes, allocation delta 2097152 bytes"));
        }
        finally
        {
            Marshal.FreeHGlobal(settingsBefore);
            Marshal.FreeHGlobal(settingsAfter);
        }
    }

    [TestMethod]
    public void Run_UsnGrow_NotAGrow_IsRefusedByTheLibraryAndNothingIsRequested()
    {
        var current = BuildJournalInfo(0x400000, 0x200000);
        try
        {
            MFTLibNative._queryUsnJournal = _ => current;
            MFTLibNative._freeUsnJournalInfo = _ => { };
            Kernel32._deviceIoControl = (_, _, _, _, _, _, out _, _) =>
                throw new AssertFailedException("A refused shrink must not reach the control request.");
            var lines = new List<string>();

            ElevatedScanner(lines).Run(["usn-grow", "T", "--maximum-size", "1048576", "--allocation-delta", "1024"]);

            Assert.IsTrue(lines.Any(line => line.StartsWith("Error on drive T: Refusing to resize", StringComparison.Ordinal)));
            Assert.IsFalse(lines.Any(line => line.StartsWith("After:", StringComparison.Ordinal)));
        }
        finally
        {
            Marshal.FreeHGlobal(current);
        }
    }

    static IntPtr BuildJournalInfo(ulong maximumSize, ulong allocationDelta)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(new UsnJournalInfoNative
        {
            JournalId = 7,
            NextUsn = 200,
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta
        }, pointer, false);
        return pointer;
    }

    static DriveScanner ElevatedScanner(List<string> lines)
    {
        return new DriveScanner
        {
            _isElevated = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
            _writeLine = lines.Add
        };
    }
}
