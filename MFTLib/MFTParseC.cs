using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

public class MFTParseC
{
    static readonly int k_OffsetToFileRecordBuffer = Marshal.OffsetOf<NTFS_FILE_RECORD_OUTPUT_BUFFER>(nameof(NTFS_FILE_RECORD_OUTPUT_BUFFER.FileRecordBuffer)).ToInt32();
    static readonly int k_OffsetToFileNameUnicode = Marshal.OffsetOf<FileNameAttribute>(nameof(FileNameAttribute.FileName)).ToInt32();

    // ReSharper disable InconsistentNaming
    const uint GENERIC_READ = 0x80000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    const uint FSCTL_GET_NTFS_FILE_RECORD = 0x00090068;
    // ReSharper restore InconsistentNaming

    public static MFTNode GetMFTNode(string volume)
    {
        if (string.IsNullOrEmpty(volume))
        {
            throw new ArgumentException("Volume name cannot be null or empty", nameof(volume));
        }

#if DEBUG
        var stopwatch = new Stopwatch();
        stopwatch.Start();
#endif

        var volumeHandle = Kernel32.CreateFile(
            volume,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            throw new IOException($"Unable to open volume {volume}", Marshal.GetLastWin32Error());
        }

        var files = ExtractMFTRecords(volumeHandle);
#if DEBUG
        Console.WriteLine($"Found {files.Length} files in {stopwatch.Elapsed}");
        //foreach (var file in files)
        //{
        //    Console.WriteLine(file.FileName);
        //}
#endif
        return null;
    }

    private static MFTLibFile[] ExtractMFTRecords(SafeFileHandle volumeHandle)
    {
        var volumeData = new NTFS_VOLUME_DATA_BUFFER();
        uint bytesReturned;
        var volumeDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(volumeData));

        try
        {
            if (!Kernel32.DeviceIoControl(
                    volumeHandle,
                    FSCTL_GET_NTFS_VOLUME_DATA,
                    IntPtr.Zero,
                    0,
                    volumeDataPtr,
                    (uint)Marshal.SizeOf(volumeData),
                    out bytesReturned,
                    IntPtr.Zero))
            {
                throw new IOException("Failed to get NTFS volume data", Marshal.GetLastWin32Error());
            }

            volumeData = Marshal.PtrToStructure<NTFS_VOLUME_DATA_BUFFER>(volumeDataPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(volumeDataPtr);
        }

#if DEBUG
        // Print out all volume data
        Console.WriteLine($"Volume Serial Number: {volumeData.VolumeSerialNumber}");
        Console.WriteLine($"Number of Sectors: {volumeData.NumberSectors}");
        Console.WriteLine($"Total Clusters: {volumeData.TotalClusters}");
        Console.WriteLine($"Free Clusters: {volumeData.FreeClusters}");
        Console.WriteLine($"Total Reserved: {volumeData.TotalReserved}");
        Console.WriteLine($"Bytes Per Sector: {volumeData.BytesPerSector}");
        Console.WriteLine($"Bytes Per Cluster: {volumeData.BytesPerCluster}");
        Console.WriteLine($"Bytes Per File Record Segment: {volumeData.BytesPerFileRecordSegment}");
        Console.WriteLine($"Clusters Per File Record Segment: {volumeData.ClustersPerFileRecordSegment}");
        Console.WriteLine($"MFT Valid Data Length: {volumeData.MftValidDataLength}");
        Console.WriteLine($"MFT Start LCN: {volumeData.MftStartLcn}");
        Console.WriteLine($"MFT2 Start LCN: {volumeData.Mft2StartLcn}");
        Console.WriteLine($"MFT Zone Start: {volumeData.MftZoneStart}");
        Console.WriteLine($"MFT Zone End: {volumeData.MftZoneEnd}");
#endif

        // Assume 1024 bytes per file record for fastpath
        // TODO: Slow path for other file record sizes? Just implement a 512 and 2048 byte struct?
        var bytesPerFileRecord = volumeData.BytesPerFileRecordSegment;
        if (bytesPerFileRecord != 1024)
            throw new NotSupportedException($"Unsupported bytes per file record: {bytesPerFileRecord}");

        var totalFileRecords = volumeData.MftValidDataLength / volumeData.BytesPerFileRecordSegment;

        Console.WriteLine($"Progress: 0 / {totalFileRecords}");
        //var files = new List<MFTLibFile>((int)totalFileRecords);
        var files = new MFTLibFile[(int)totalFileRecords];
        unsafe
        {
            fixed (void* ptr = files)
            {
                MFTLibC.ExtractMFTRecords(volumeHandle, ptr, totalFileRecords);
            }
        }

        return files;
    }
}