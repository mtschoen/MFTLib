using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MFTLib.Index;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Creates a 4 GiB dynamically expanding VHDX in a test-owned temporary directory,
///     attaches it through <c>virtdisk.dll</c> (no Hyper-V requirement), initializes an
///     MBR disk, creates and NTFS-formats a partition with an active 64 MiB USN journal
///     (8 MiB allocation delta), exposes its assigned <see cref="VolumeKey" />, and detaches
///     plus deletes the image on disposal even when a test fails.
/// </summary>
sealed class TempNtfsVhdx : IDisposable
{
    // 4 GiB virtual capacity, dynamically expanding, so only written blocks use host disk. The
    // 5 percent recommended USN journal cap is then about 204 MiB. The seeded journal is 64 MiB
    // with an 8 MiB delta: on Windows 11 build 26300, NTFS reported success for in-place resizes
    // of 1 MiB and 2 MiB journals but kept the old maximum, while 64 MiB journals resized.
    public const ulong DefaultDiskSizeBytes = 4UL * 1024 * 1024 * 1024;
    public const long InitialJournalMaximumSizeBytes = 64L * 1024 * 1024;
    public const long InitialJournalAllocationDeltaBytes = 8L * 1024 * 1024;

    const uint VirtualStorageTypeDeviceVhdx = 3;
    const uint CreateVirtualDiskVersion1 = 1;
    const uint CreateVirtualDiskFlagNone = 0;
    const uint VirtualDiskAccessAll = 0x003f0000;
    const uint AttachVirtualDiskFlagNone = 0;

    static readonly Guid VirtualStorageTypeVendorMicrosoft =
        new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    readonly TempDirectory _directory;
    readonly SafeFileHandle _virtualDiskHandle;
    int _disposed;

    TempNtfsVhdx(TempDirectory directory, SafeFileHandle virtualDiskHandle, VolumeKey volume)
    {
        _directory = directory;
        _virtualDiskHandle = virtualDiskHandle;
        Volume = volume;
    }

    public VolumeKey Volume { get; }

    public static TempNtfsVhdx Create(ulong diskSizeBytes = DefaultDiskSizeBytes)
    {
        var directory = new TempDirectory("mftlib-vhdx");
        var vhdxPath = Path.Combine(directory.Path, "volume.vhdx");
        var storageType = new VirtualStorageType
        {
            DeviceId = VirtualStorageTypeDeviceVhdx,
            VendorId = VirtualStorageTypeVendorMicrosoft
        };
        var parameters = new CreateVirtualDiskParameters
        {
            Version = CreateVirtualDiskVersion1,
            MaximumSize = diskSizeBytes
        };

        var createError = CreateVirtualDisk(
            ref storageType,
            vhdxPath,
            VirtualDiskAccessAll,
            IntPtr.Zero,
            CreateVirtualDiskFlagNone,
            0,
            ref parameters,
            IntPtr.Zero,
            out var virtualDiskHandle);
        if (createError != 0)
        {
            virtualDiskHandle.Dispose();
            directory.Dispose();
            throw new Win32Exception(
                (int)createError,
                $"CreateVirtualDisk failed for '{vhdxPath}' with Win32 error {createError}.");
        }

        try
        {
            var attachError = AttachVirtualDisk(
                virtualDiskHandle,
                IntPtr.Zero,
                AttachVirtualDiskFlagNone,
                0,
                IntPtr.Zero,
                IntPtr.Zero);
            if (attachError != 0)
            {
                throw new Win32Exception(
                    (int)attachError,
                    $"AttachVirtualDisk failed for '{vhdxPath}' with Win32 error {attachError}.");
            }

            var diskNumber = GetPhysicalDiskNumber(virtualDiskHandle);
            var volume = InitializePartitionAndFormatNtfs(diskNumber, vhdxPath);
            return new TempNtfsVhdx(directory, virtualDiskHandle, volume);
        }
        catch
        {
            DetachAndDelete(directory, virtualDiskHandle);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DetachAndDelete(_directory, _virtualDiskHandle);
    }

    static void DetachAndDelete(TempDirectory directory, SafeFileHandle virtualDiskHandle)
    {
        try
        {
            try
            {
                if (!virtualDiskHandle.IsClosed && !virtualDiskHandle.IsInvalid)
                {
                    _ = DetachVirtualDisk(virtualDiskHandle, 0, 0);
                }
            }
            finally
            {
                virtualDiskHandle.Dispose();
            }
        }
        finally
        {
            directory.Dispose();
        }
    }

    static uint GetPhysicalDiskNumber(SafeFileHandle virtualDiskHandle)
    {
        var pathBuffer = new char[260];
        var pathSizeInBytes = (uint)(pathBuffer.Length * sizeof(char));
        var error = GetVirtualDiskPhysicalPath(virtualDiskHandle, ref pathSizeInBytes, pathBuffer);
        if (error != 0)
        {
            throw new Win32Exception(
                (int)error,
                $"GetVirtualDiskPhysicalPath failed with Win32 error {error}.");
        }

        var nullTerminator = Array.IndexOf(pathBuffer, '\0');
        var physicalPath = nullTerminator >= 0
            ? new string(pathBuffer, 0, nullTerminator)
            : new string(pathBuffer);
        const string physicalDrivePrefix = @"\\.\PhysicalDrive";
        if (!physicalPath.StartsWith(physicalDrivePrefix, StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(
                physicalPath.AsSpan(physicalDrivePrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var diskNumber))
        {
            throw new InvalidOperationException(
                $"Unexpected virtual disk physical path '{physicalPath}'.");
        }

        return diskNumber;
    }

    static VolumeKey InitializePartitionAndFormatNtfs(uint diskNumber, string vhdxPath)
    {
        // The script refuses to touch any disk that is not this test's own file-backed VHDX,
        // so a wrong disk number can never partition or format a real drive.
        var quotedVhdxPath = "'" + vhdxPath.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            Import-Module (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules\Storage\Storage.psd1') -ErrorAction Stop
            $diskNumber = {{diskNumber}}
            $disk = Get-Disk -Number $diskNumber -ErrorAction SilentlyContinue
            if ($null -eq $disk) {
                $null = Update-HostStorageCache
                $disk = Get-Disk -Number $diskNumber -ErrorAction Stop
            }
            $expectedLocation = [System.IO.Path]::GetFullPath({{quotedVhdxPath}})
            if ([string]$disk.BusType -ne 'File Backed Virtual' -or
                -not [string]::Equals([string]$disk.Location, $expectedLocation, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Disk $diskNumber is not the test VHDX (bus type '$($disk.BusType)', location '$($disk.Location)'); refusing to partition it."
            }
            if ($disk.IsOffline) {
                $null = Set-Disk -Number $diskNumber -IsOffline $false
            }
            if ($disk.IsReadOnly) {
                $null = Set-Disk -Number $diskNumber -IsReadOnly $false
            }
            if ($disk.PartitionStyle -eq 'RAW') {
                $null = Initialize-Disk -Number $diskNumber -PartitionStyle MBR -Confirm:$false -ErrorAction Stop
            }
            $partition = New-Partition -DiskNumber $diskNumber -UseMaximumSize -ErrorAction Stop
            $null = Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel 'MFTLibVHDX' -Force -Confirm:$false -ErrorAction Stop
            $null = Add-PartitionAccessPath -DiskNumber $diskNumber -PartitionNumber $partition.PartitionNumber -AssignDriveLetter -ErrorAction Stop
            $driveLetter = [string](Get-Partition -DiskNumber $diskNumber -PartitionNumber $partition.PartitionNumber -ErrorAction Stop).DriveLetter
            if ([string]::IsNullOrWhiteSpace($driveLetter)) {
                throw 'Formatted VHDX partition did not receive a drive letter.'
            }
            $volumeSpec = "$($driveLetter):"
            $null = & fsutil.exe usn deletejournal /n $volumeSpec 2>&1
            $createOutput = & fsutil.exe usn createjournal $volumeSpec m={{InitialJournalMaximumSizeBytes}} a={{InitialJournalAllocationDeltaBytes}} 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "fsutil usn createjournal failed ($LASTEXITCODE): $createOutput"
            }
            Write-Output $driveLetter
            """);

        var startInfo = new ProcessStartInfo(DefenderExclusionSequence.PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Windows PowerShell to format the test VHDX.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var standardOutput = outputTask.GetAwaiter().GetResult().Trim();
        var standardError = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Formatting test VHDX on disk {diskNumber} failed (exit {process.ExitCode}): {standardError} {standardOutput}".Trim());
        }

        if (!VolumeKey.TryParse(standardOutput, out var volume))
        {
            throw new InvalidOperationException(
                $"PowerShell VHDX setup returned an invalid drive letter '{standardOutput}'.");
        }

        return volume;
    }

    [DllImport("virtdisk.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern uint CreateVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref CreateVirtualDiskParameters parameters,
        IntPtr overlapped,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    static extern uint AttachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        IntPtr parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle virtualDiskHandle,
        ref uint diskPathSizeInBytes,
        [Out] char[] diskPath);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    static extern uint DetachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        uint flags,
        uint providerSpecificFlags);

    [StructLayout(LayoutKind.Sequential)]
    struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    // Mirrors CREATE_VIRTUAL_DISK_PARAMETERS (CREATE_VIRTUAL_DISK_VERSION_1) on 64-bit Windows:
    // Version (4) + padding (4) + UniqueId (16) + MaximumSize (8) + BlockSizeInBytes (4) +
    // SectorSizeInBytes (4) + ParentPath (8) + SourcePath (8) = 56 bytes.
    [StructLayout(LayoutKind.Sequential)]
    struct CreateVirtualDiskParameters
    {
        public uint Version;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public IntPtr ParentPath;
        public IntPtr SourcePath;
    }
}
