using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.Index;

/// <summary>
///     The volume serial an open resolves without elevation: the full 64 bits <c>FILE_ID_INFO</c> reports on the
///     volume's root directory, or the zero-extended 32-bit serial of a file system that refuses that query.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PlatformVolumeSerialTests
{
    const ulong FullSerial = 0x088E_CC4B_8ECC_32CC;
    const uint LowerHalf = 0x8ECC_32CC;

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        Kernel32.Hooks.ResetToDefaults();
    }

    [TestMethod]
    public void ReadSerial_ReturnsAllSixtyFourBitsReadOnAnAttributesOnlyRootDirectoryHandle()
    {
        var opened = new List<(string Path, uint Access, uint Flags)>();
        Kernel32.Hooks._createFile = (path, access, _, _, _, flags, _) =>
        {
            opened.Add((path, access, flags));
            return new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        };
        Kernel32.Hooks._readVolumeSerialByHandle = (_, out serial) =>
        {
            serial = FullSerial;
            return true;
        };
        Kernel32.Hooks._readVolumeSerialByRoot = (_, out serial) =>
        {
            serial = LowerHalf;
            Assert.Fail("A volume that reports its full serial must not be read through the 32-bit query.");
            return true;
        };

        var serial = PlatformVolumeFacts.ReadSerial(@"C:\", "NTFS");

        Assert.AreEqual(FullSerial, serial);
        const uint fileReadAttributes = 0x80;
        const uint fileFlagBackupSemantics = 0x02000000;
        Assert.AreSequenceEqual([(@"C:\", fileReadAttributes, fileFlagBackupSemantics)], opened);
    }

    [TestMethod]
    [DataRow("FAT32", true)]
    [DataRow("exFAT", true)]
    [DataRow("FAT", false)]
    public void ReadSerial_ThirtyTwoBitFileSystemRefusingTheFullQuery_ZeroExtendsItsSerial(string format,
        bool rootOpens)
    {
        Kernel32.Hooks._createFile = (_, _, _, _, _, _, _) =>
            new SafeFileHandle(new IntPtr(rootOpens ? 1 : -1), ownsHandle: false);
        Kernel32.Hooks._readVolumeSerialByHandle = (_, out serial) =>
        {
            serial = 0;
            return false;
        };
        var queried = new List<string>();
        Kernel32.Hooks._readVolumeSerialByRoot = (path, out serial) =>
        {
            queried.Add(path);
            serial = 0x9ABC_DEF0;
            return true;
        };

        var serial = PlatformVolumeFacts.ReadSerial(@"E:\", format);

        Assert.AreEqual(0x0000_0000_9ABC_DEF0ul, serial);
        Assert.AreSequenceEqual([@"E:\"], queried);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReadSerial_NtfsRefusingTheFullQuery_ThrowsInsteadOfReportingTheLowerHalf(bool rootOpens)
    {
        Kernel32.Hooks._createFile = (_, _, _, _, _, _, _) =>
            new SafeFileHandle(new IntPtr(rootOpens ? 1 : -1), ownsHandle: false);
        Kernel32.Hooks._readVolumeSerialByHandle = (_, out serial) =>
        {
            serial = 0;
            return false;
        };
        var queried = 0;
        Kernel32.Hooks._readVolumeSerialByRoot = (_, out serial) =>
        {
            queried++;
            serial = LowerHalf;
            return true;
        };

        var failure = Assert.ThrowsExactly<IOException>(() => PlatformVolumeFacts.ReadSerial(@"C:\", "NTFS"));

        Assert.Contains(@"C:\", failure.Message);
        Assert.AreEqual(0, queried);
    }

    [TestMethod]
    public void ReadSerial_ThirtyTwoBitFileSystemRefusingBothQueries_Throws()
    {
        Kernel32.Hooks._createFile = (_, _, _, _, _, _, _) => new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        Kernel32.Hooks._readVolumeSerialByHandle = (_, out serial) =>
        {
            serial = 0;
            return false;
        };
        Kernel32.Hooks._readVolumeSerialByRoot = (_, out serial) =>
        {
            serial = 0;
            return false;
        };

        Assert.ThrowsExactly<IOException>(() => PlatformVolumeFacts.ReadSerial(@"E:\", "FAT32"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Read_ResolvesTheRootsVolumeSerialThroughTheSeamOnce()
    {
        using var temp = new TempDirectory("mftlib-volume-serial");
        var reads = 0;
        Kernel32.Hooks._readVolumeSerialByHandle = (_, out serial) =>
        {
            reads++;
            serial = FullSerial;
            return true;
        };

        var facts = PlatformVolumeFacts.Read(RootKey.FromDirectory(temp.Path), temp.Path);

        Assert.IsTrue(facts.IsReady);
        Assert.AreEqual(FullSerial, facts.VolumeSerial);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    [DataRow(ProducerSelection.Automatic, MftUnavailableReason.VolumeNotReady)]
    [DataRow(ProducerSelection.WalkOnly, null)]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Read_NtfsSerialRefusal_WalksWithoutCachingAndDeclinesCacheOnlyOpen(
        ProducerSelection producer, MftUnavailableReason? expectedReason)
    {
        using var temp = new TempDirectory("mftlib-volume-serial-refusal");
        using var cache = new TempDirectory("mftlib-volume-serial-refusal-cache");
        var volumeRoot = HandleBoundFile.VolumePathOf(temp.Path);
        if (new DriveInfo(volumeRoot).DriveFormat != "NTFS")
        {
            Assert.Inconclusive($"The temporary directory is on {new DriveInfo(volumeRoot).DriveFormat}, not NTFS.");
        }

        var token = TestContext.CancellationToken;
        var options = new FileIndexOptions
        {
            Root = RootKey.FromDirectory(temp.Path),
            CacheDirectory = cache.Path,
            CacheMode = CacheMode.Persistent,
            Producer = producer
        };
        var oldPath = Path.Combine(temp.Path, "old.txt");
        var newPath = Path.Combine(temp.Path, "new.txt");
        await File.WriteAllTextAsync(oldPath, "marker", token);
        Kernel32.Hooks._readVolumeSerialByHandle = (_, out serial) =>
        {
            serial = 0;
            return true;
        };
        Kernel32.Hooks._readVolumeSerialByRoot = (_, out serial) =>
        {
            serial = 0;
            Assert.Fail("An NTFS serial refusal must not fall back to the 32-bit query.");
            return false;
        };
        using var seededCache = new TempDirectory("mftlib-volume-serial-seeded-cache");
        await using (var seed = await FileIndex.OpenAsync(options with
        {
            Producer = ProducerSelection.WalkOnly,
            CacheDirectory = seededCache.Path
        }, token))
        {
            Assert.AreEqual(CacheLocation.OwnedCanonical, seed.Status.Block?.CacheLocation);
            Assert.IsNotNull(seed.Find(oldPath, token));
        }

        Kernel32.Hooks._readVolumeSerialByHandle = (_, out serial) =>
        {
            serial = 0;
            return false;
        };
        await using (var index = await FileIndex.OpenAsync(options, token))
        {
            Assert.AreEqual(RootState.Ready, index.Status.State);
            Assert.AreEqual(expectedReason, index.Status.MftUnavailable);
            Assert.AreEqual(CacheLocation.PrivateFallback, index.Status.Block!.CacheLocation);
            File.Move(oldPath, newPath);
            await index.RescanAsync(token);
            Assert.IsNotNull(index.Find(newPath, token));
            Assert.IsNull(index.Find(oldPath, token));
            Assert.AreEqual(CacheLocation.PrivateFallback, index.Status.Block.CacheLocation);
            Assert.IsEmpty(Directory.GetFiles(cache.Path));
        }

        await using var cacheOnly = await FileIndex.OpenAsync(options with
        {
            CacheDirectory = seededCache.Path,
            CacheMode = CacheMode.ExistingOnly
        }, token);
        Assert.AreEqual(RootState.Failed, cacheOnly.Status.State);
        Assert.AreEqual(RootFailureKind.CacheDeclined, cacheOnly.Status.Failure?.Kind);
        Assert.HasCount(1, Directory.GetFiles(seededCache.Path, "*.mlix"));
    }

    /// <summary>
    ///     Unelevated, on the real volume holding the temporary directory: the resolved serial is the one
    ///     <c>FSCTL_GET_NTFS_VOLUME_DATA</c> reports, which the elevated scan reads from the volume handle, and its
    ///     lower half is the <c>GetVolumeInformationW</c> serial.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Read_OnALocalNtfsVolume_ResolvesTheSerialTheNtfsVolumeDataReports()
    {
        using var temp = new TempDirectory("mftlib-volume-serial-live");
        var volumeRoot = HandleBoundFile.VolumePathOf(temp.Path);
        var format = new DriveInfo(volumeRoot).DriveFormat;
        if (format != "NTFS")
        {
            Assert.Inconclusive($"The temporary directory is on {format}, not NTFS.");
        }

        var facts = PlatformVolumeFacts.Read(RootKey.FromDirectory(temp.Path), temp.Path);
        Assert.IsTrue(facts.IsReady);
        Assert.AreEqual("NTFS", facts.Format);
        Assert.IsTrue(Kernel32.Hooks._readVolumeSerialByRoot(facts.VolumeRootDirectory!, out var lowerHalf));
        Assert.AreEqual(lowerHalf, (uint)facts.VolumeSerial);
        Assert.AreEqual(ReadNtfsVolumeDataSerial(facts.VolumeRootDirectory!), facts.VolumeSerial);
    }

    [SupportedOSPlatform("windows")]
    static ulong ReadNtfsVolumeDataSerial(string volumeRootDirectory)
    {
        const uint fsctlGetNtfsVolumeData = 0x00090064;
        using var root = HandleBoundFile.OpenDirectoryFollowingLinks(volumeRootDirectory);
        var size = Marshal.SizeOf<NtfsVolumeDataBufferNative>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Assert.IsTrue(Kernel32.Hooks._deviceIoControl(root, fsctlGetNtfsVolumeData, IntPtr.Zero, 0, buffer,
                (uint)size, out _, IntPtr.Zero), $"FSCTL_GET_NTFS_VOLUME_DATA failed: {Marshal.GetLastPInvokeError()}");
            return unchecked((ulong)Marshal.PtrToStructure<NtfsVolumeDataBufferNative>(buffer).VolumeSerialNumber);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
