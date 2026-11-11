using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

public partial class UsnJournalSyntheticTests
{
    [TestMethod]
    [DataRow(40UL * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024)]
    [DataRow(100UL * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024)]
    [DataRow(160UL * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    [DataRow(1024UL * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    [DataRow(4096UL * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    [DataRow(20_000_039UL, 1_000_001L)]
    public void QueryMaximumRecommendedSize_MatchesPolicyOnSmallAndLargeVolumes(ulong volumeBytes, long expectedCapBytes)
    {
        UseDiskSpace(volumeBytes);

        var maximumRecommended = UsnJournalSettings.QueryMaximumRecommendedSize('c');

        Assert.AreEqual(expectedCapBytes, maximumRecommended);
    }

    [TestMethod]
    public void QueryMaximumRecommendedSize_DefaultVolume_ThrowsBeforeQueryingDiskSpace()
    {
        var diskSpaceCalls = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            Interlocked.Increment(ref diskSpaceCalls);
            diskSpaceInformation = default;
            return 0;
        };

        Assert.ThrowsExactly<ArgumentException>(() => UsnJournalSettings.QueryMaximumRecommendedSize(default));
        Assert.AreEqual(0, diskSpaceCalls);
    }

    [TestMethod]
    [DataRow(0x200000L, false)]
    [DataRow(0x100000L, false)]
    [DataRow(0x200000L, true)]
    [DataRow(0x100000L, true)]
    public void GrowUsnJournal_RequestedNotLargerThanCurrent_ReturnsAlreadyAtOrAboveTargetWithoutCreateIoctl(
        long requestedMaximumSize,
        bool allowAboveMaximumRecommendedSize)
    {
        UseFakeHandle();
        UseDiskSpace(100UL * 1024 * 1024 * 1024);
        var query = JournalQueryFake.Create(() =>
            JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000));
        var createCalled = false;
        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            createCalled = true;
            bytesReturned = 0;
            return true;
        }

        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;
        using var volume = MftVolume.Open('C');
        var result = volume.Journal.GrowUsnJournal(requestedMaximumSize, 0x100000, allowAboveMaximumRecommendedSize);

        Assert.IsFalse(createCalled);
        Assert.AreEqual(UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget, result.Outcome);
        Assert.AreEqual(requestedMaximumSize, result.RequestedMaximumSize);
        Assert.AreEqual(5L * 1024 * 1024 * 1024, result.MaximumRecommendedSize);
        Assert.AreEqual(0x200000L, result.JournalSettings.MaximumSize);
        Assert.AreEqual(0x100000L, result.JournalSettings.AllocationDelta);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GrowUsnJournal_ValidRequest_IssuesCreateAndReturnsPostChangeSettings(bool allowAboveMaximumRecommendedSize)
    {
        UseFakeHandle();
        UseDiskSpace(100UL * 1024 * 1024 * 1024);
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000)
            : JournalQueryFake.Describe(7, 200, maximumSize: 0x400000, allocationDelta: 0x200000));
        uint? capturedCode = null;
        long capturedMaximum = 0;
        long capturedDelta = 0;
        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            capturedCode = ioControlCode;
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            capturedDelta = Marshal.ReadInt64(inBuffer, 8);
            bytesReturned = 0;
            return true;
        }

        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;
        using var volume = MftVolume.Open('C');
        var result = volume.Journal.GrowUsnJournal(0x400000, 0x200000, allowAboveMaximumRecommendedSize);

        Assert.AreEqual(0x000900E7u, capturedCode);
        Assert.AreEqual(0x400000L, capturedMaximum);
        Assert.AreEqual(0x200000L, capturedDelta);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAsRequested, result.Outcome);
        Assert.AreEqual(0x400000L, result.RequestedMaximumSize);
        Assert.AreEqual(5L * 1024 * 1024 * 1024, result.MaximumRecommendedSize);
        Assert.AreEqual(0x400000L, result.JournalSettings.MaximumSize);
        Assert.AreEqual(0x200000L, result.JournalSettings.AllocationDelta);
    }

    [TestMethod]
    public void GrowUsnJournal_CreateFails_ThrowsWin32Exception()
    {
        UseFakeHandle();
        UseDiskSpace(100UL * 1024 * 1024 * 1024);
        var query = JournalQueryFake.Create(() =>
            JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000));
        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            bytesReturned = 0;
            return false;
        }

        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        Assert.ThrowsExactly<Win32Exception>(() => journal.GrowUsnJournal(0x400000, 0x100000));
    }

    [TestMethod]
    public void GrowUsnJournal_NonPositiveSizes_ThrowBeforeAnyIoctl()
    {
        UseFakeHandle();
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => journal.GrowUsnJournal(0, 0x100000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => journal.GrowUsnJournal(0x400000, 0));
    }

    [TestMethod]
    [DataRow(100UL * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, false)]
    [DataRow(100UL * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, true)]
    [DataRow(160UL * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024, false)]
    [DataRow(1024UL * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024, false)]
    [DataRow(20_000_039UL, 1_000_001L, false)]
    public void GrowUsnJournal_AtExactCap_IssuesCreateAndReturnsSettings(
        ulong volumeBytes,
        long exactCapBytes,
        bool allowAboveMaximumRecommendedSize)
    {
        UseFakeHandle();
        UseDiskSpace(volumeBytes);
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x1000, allocationDelta: 0x1000)
            : JournalQueryFake.Describe(7, 200, maximumSize: (ulong)exactCapBytes, allocationDelta: 0x1000));
        long capturedMaximum = 0;
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var result = volume.Journal.GrowUsnJournal(exactCapBytes, 0x1000, allowAboveMaximumRecommendedSize);

        Assert.AreEqual(exactCapBytes, capturedMaximum);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAsRequested, result.Outcome);
        Assert.AreEqual(exactCapBytes, result.RequestedMaximumSize);
        Assert.AreEqual(exactCapBytes, result.MaximumRecommendedSize);
        Assert.AreEqual(exactCapBytes, result.JournalSettings.MaximumSize);
    }

    [TestMethod]
    [DataRow(100UL * 1024 * 1024 * 1024, (5L * 1024 * 1024 * 1024) + 1, 5L * 1024 * 1024 * 1024)]
    [DataRow(160UL * 1024 * 1024 * 1024, (8L * 1024 * 1024 * 1024) + 1, 8L * 1024 * 1024 * 1024)]
    [DataRow(1024UL * 1024 * 1024 * 1024, (8L * 1024 * 1024 * 1024) + 1, 8L * 1024 * 1024 * 1024)]
    [DataRow(20_000_039UL, 1_000_002L, 1_000_001L)]
    [DataRow(1024UL * 1024 * 1024 * 1024, long.MaxValue, 8L * 1024 * 1024 * 1024)]
    public void GrowUsnJournal_AboveCapWithoutOverride_ClampsToCapAndReportsClampedOutcome(
        ulong volumeBytes,
        long requestedMaximumSize,
        long expectedRecommendedBytes)
    {
        UseFakeHandle();
        UseDiskSpace(volumeBytes);
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x1000, allocationDelta: 0x1000)
            : JournalQueryFake.Describe(7, 200, maximumSize: (ulong)expectedRecommendedBytes, allocationDelta: 0x1000));
        long capturedMaximum = 0;
        var createCalls = 0;
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            Interlocked.Increment(ref createCalls);
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var result = volume.Journal.GrowUsnJournal(requestedMaximumSize, 0x1000);

        Assert.AreEqual(1, createCalls);
        Assert.AreEqual(expectedRecommendedBytes, capturedMaximum);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended, result.Outcome);
        Assert.AreEqual(requestedMaximumSize, result.RequestedMaximumSize);
        Assert.AreEqual(expectedRecommendedBytes, result.MaximumRecommendedSize);
        Assert.AreEqual(expectedRecommendedBytes, result.JournalSettings.MaximumSize);
    }

    [TestMethod]
    [DataRow(5L * 1024 * 1024 * 1024)]
    [DataRow(6L * 1024 * 1024 * 1024)]
    public void GrowUsnJournal_AboveCapWithoutOverride_WhenCurrentAtOrAboveCap_ReturnsAlreadyAtOrAboveTargetWithoutCreateIoctl(
        long currentMaximumBytes)
    {
        UseFakeHandle();
        UseDiskSpace(100UL * 1024 * 1024 * 1024); // 5 GiB cap
        const long expectedRecommendedBytes = 5L * 1024 * 1024 * 1024;
        const long requestedMaximumSize = expectedRecommendedBytes + (2L * 1024 * 1024 * 1024);
        var query = JournalQueryFake.Create(() =>
            JournalQueryFake.Describe(7, 200, maximumSize: (ulong)currentMaximumBytes, allocationDelta: 0x100000));
        var createCalls = 0;
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Interlocked.Increment(ref createCalls);
            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var result = volume.Journal.GrowUsnJournal(requestedMaximumSize, 0x100000);

        Assert.AreEqual(0, createCalls);
        Assert.AreEqual(UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget, result.Outcome);
        Assert.AreEqual(requestedMaximumSize, result.RequestedMaximumSize);
        Assert.AreEqual(expectedRecommendedBytes, result.MaximumRecommendedSize);
        Assert.AreEqual(currentMaximumBytes, result.JournalSettings.MaximumSize);
    }

    [TestMethod]
    [DataRow(100UL * 1024 * 1024 * 1024, (5L * 1024 * 1024 * 1024) + 1, 5L * 1024 * 1024 * 1024)]
    [DataRow(4096UL * 1024 * 1024 * 1024, 11L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    [DataRow(20_000_039UL, 1_000_002L, 1_000_001L)]
    public void GrowUsnJournal_AboveCapWithOverride_AppliesFullSizeAndReportsOverrideOutcome(
        ulong volumeBytes,
        long requestedMaximumSize,
        long expectedRecommendedBytes)
    {
        UseFakeHandle();
        UseDiskSpace(volumeBytes);
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x1000, allocationDelta: 0x1000)
            : JournalQueryFake.Describe(7, 200, maximumSize: (ulong)requestedMaximumSize, allocationDelta: 0x1000));
        long capturedMaximum = 0;
        var createCalls = 0;
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            Interlocked.Increment(ref createCalls);
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var result = volume.Journal.GrowUsnJournal(requestedMaximumSize, 0x1000, allowAboveMaximumRecommendedSize: true);

        Assert.AreEqual(1, createCalls);
        Assert.AreEqual(requestedMaximumSize, capturedMaximum);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride, result.Outcome);
        Assert.AreEqual(requestedMaximumSize, result.RequestedMaximumSize);
        Assert.AreEqual(expectedRecommendedBytes, result.MaximumRecommendedSize);
        Assert.AreEqual(requestedMaximumSize, result.JournalSettings.MaximumSize);
    }

    [TestMethod]
    public void GrowUsnJournal_QueriesVolumeSizeAtRequestTimeWithoutCaching()
    {
        UseFakeHandle();
        var volumeSizes = new Queue<ulong>([40UL * 1024 * 1024 * 1024, 100UL * 1024 * 1024 * 1024]);
        var diskSpaceQueries = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (rootPath, out diskSpaceInformation) =>
        {
            Assert.AreEqual(@"C:\", rootPath);
            Interlocked.Increment(ref diskSpaceQueries);
            diskSpaceInformation = new DiskSpaceInformation
            {
                ActualTotalAllocationUnits = volumeSizes.Dequeue(),
                SectorsPerAllocationUnit = 1,
                BytesPerSector = 1
            };
            return 0;
        };
        const long twoGiB = 2L * 1024 * 1024 * 1024;
        const long threeGiB = 3L * 1024 * 1024 * 1024;
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount switch
        {
            1 => JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000),
            2 or 3 => JournalQueryFake.Describe(7, 200, maximumSize: twoGiB, allocationDelta: 0x100000),
            _ => JournalQueryFake.Describe(7, 200, maximumSize: threeGiB, allocationDelta: 0x100000)
        });
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var first = journal.GrowUsnJournal(threeGiB, 0x100000);
        Assert.AreEqual(1, diskSpaceQueries);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended, first.Outcome);
        Assert.AreEqual(twoGiB, first.MaximumRecommendedSize);
        Assert.AreEqual(twoGiB, first.JournalSettings.MaximumSize);

        var second = journal.GrowUsnJournal(threeGiB, 0x100000);

        Assert.AreEqual(2, diskSpaceQueries);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAsRequested, second.Outcome);
        Assert.AreEqual(5L * 1024 * 1024 * 1024, second.MaximumRecommendedSize);
        Assert.AreEqual(threeGiB, second.JournalSettings.MaximumSize);
    }

    [TestMethod]
    public void GrowUsnJournal_IgnoresCallerQuotaAndAvailableSpaceFields()
    {
        UseFakeHandle();
        // 100 GiB total volume (5 GiB cap), but caller quota and available space are only 1 KiB.
        UseDiskSpace(100UL * 1024 * 1024 * 1024,
            callerTotalAllocationUnits: 1024,
            actualAvailableAllocationUnits: 1024,
            callerAvailableAllocationUnits: 1024);
        const long fiveGiB = 5L * 1024 * 1024 * 1024;
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000)
            : JournalQueryFake.Describe(7, 200, maximumSize: fiveGiB, allocationDelta: 0x100000));
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var result = volume.Journal.GrowUsnJournal(fiveGiB, 0x100000);

        Assert.AreEqual(fiveGiB, result.JournalSettings.MaximumSize);
    }

    [TestMethod]
    public void GrowUsnJournal_DiskSpaceQueryFails_ThrowsWin32ExceptionBeforeAnyIoctl()
    {
        UseFakeHandle();
        const int failedHresult = unchecked((int)0x80070005);
        var ioctlCalls = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            diskSpaceInformation = default;
            return failedHresult;
        };
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, _, _, out bytesReturned, _) =>
        {
            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return false;
        };

        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<Win32Exception>(() =>
            journal.GrowUsnJournal(0x400000, 0x100000));

        Assert.AreEqual(failedHresult, exception.NativeErrorCode);
        Assert.AreEqual(0, ioctlCalls);
    }

    [TestMethod]
    [DataRow(0UL, 8u, 512u)]
    [DataRow(1000UL, 0u, 512u)]
    [DataRow(1000UL, 8u, 0u)]
    [DataRow(19UL, 1u, 1u)]
    [DataRow(ulong.MaxValue, 2u, 512u)]
    public void GrowUsnJournal_UnusableGeometryOrOverflow_ThrowsInvalidOperationBeforeAnyIoctl(
        ulong totalUnits, uint sectorsPerUnit, uint bytesPerSector)
    {
        UseFakeHandle();
        var ioctlCalls = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            diskSpaceInformation = new DiskSpaceInformation
            {
                ActualTotalAllocationUnits = totalUnits,
                SectorsPerAllocationUnit = sectorsPerUnit,
                BytesPerSector = bytesPerSector
            };
            return 0;
        };
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, _, _, out bytesReturned, _) =>
        {
            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return false;
        };

        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.GrowUsnJournal(0x400000, 0x100000));

        Assert.AreEqual(0, ioctlCalls);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("C:")]
    [DataRow(@"\\.\c:")]
    [DataRow(@"\\.\C:\")]
    [DataRow(@"\\.\1:")]
    [DataRow(@"\\.\C!")]
    public void GrowUsnJournal_InvalidVolumePath_ThrowsBeforeQueryingDiskSpaceOrIoctl(string invalidVolumePath)
    {
        var diskSpaceCalls = 0;
        var ioctlCalls = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            Interlocked.Increment(ref diskSpaceCalls);
            diskSpaceInformation = default;
            return 0;
        };
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, _, _, out bytesReturned, _) =>
        {
            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return false;
        };

        using var handle = FakeVolumeHandle.Create();
        var journal = new UsnJournalVolume(handle, invalidVolumePath);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.GrowUsnJournal(0x400000, 0x100000));
        Assert.AreEqual(0, diskSpaceCalls);
        Assert.AreEqual(0, ioctlCalls);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void GetDiskSpaceInformation_OnWindowsSystemRoot_ReturnsPositiveGeometry()
    {
        var hresult = Kernel32.Hooks._getDiskSpaceInformation(@"C:\", out var diskSpace);

        Assert.AreEqual(0, hresult);
        Assert.IsGreaterThan(0UL, diskSpace.ActualTotalAllocationUnits);
        Assert.IsGreaterThan(0u, diskSpace.SectorsPerAllocationUnit);
        Assert.IsGreaterThan(0u, diskSpace.BytesPerSector);
    }
}
