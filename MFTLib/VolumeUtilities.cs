using System.Runtime.InteropServices;
using System.Text;

namespace MFTLib
{
    public static class VolumeUtilities
    {
        // ReSharper disable once InconsistentNaming
        const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

        static readonly StringBuilder k_StringBuilder = new StringBuilder(1024);

        public static NTFS_EXTENDED_VOLUME_DATA_BUFFER GetVolumeInfo(string volume)
        {
            var volumeHandle = FileUtilities.GetVolumeHandle(volume);

            var volumeData = new NTFS_EXTENDED_VOLUME_DATA_BUFFER();
            var bufferSize = Marshal.SizeOf(volumeData);
            var volumeDataPtr = Marshal.AllocHGlobal(bufferSize);

            try
            {
                if (!Kernel32.DeviceIoControl(
                        volumeHandle,
                        FSCTL_GET_NTFS_VOLUME_DATA,
                        IntPtr.Zero,
                        0,
                        volumeDataPtr,
                        (uint)Marshal.SizeOf(volumeData),
                        out var bytesReturned,
                        IntPtr.Zero))
                {
                    throw new IOException("Failed to get NTFS volume data", Marshal.GetLastWin32Error());
                }

                if (bytesReturned != bufferSize)
                    throw new InvalidOperationException($"Wrong amount of bytes returned in {nameof(GetVolumeInfo)}");

                volumeData = Marshal.PtrToStructure<NTFS_EXTENDED_VOLUME_DATA_BUFFER>(volumeDataPtr);
                return volumeData;
            }
            finally
            {
                Marshal.FreeHGlobal(volumeDataPtr);
            }
        }

        public static string DumpVolumeInfo(NTFS_EXTENDED_VOLUME_DATA_BUFFER data)
        {
            k_StringBuilder.Clear();
            var header = data.VolumeData;
            k_StringBuilder.AppendLine($"Volume Serial Number: {header.VolumeSerialNumber}");
            k_StringBuilder.AppendLine($"Number Sectors: {header.NumberSectors}");
            k_StringBuilder.AppendLine($"Total Clusters: {header.TotalClusters}");
            k_StringBuilder.AppendLine($"Free Clusters: {header.FreeClusters}");
            k_StringBuilder.AppendLine($"Total Reserved: {header.TotalReserved}");
            k_StringBuilder.AppendLine($"Bytes Per Sector: {header.BytesPerSector}");
            k_StringBuilder.AppendLine($"Bytes Per Cluster: {header.BytesPerCluster}");
            k_StringBuilder.AppendLine($"Bytes Per File Record Segment: {header.BytesPerFileRecordSegment}");
            k_StringBuilder.AppendLine($"Clusters Per File Record Segment: {header.ClustersPerFileRecordSegment}");
            k_StringBuilder.AppendLine($"MFT Valid Data Length: {header.MftValidDataLength}");
            k_StringBuilder.AppendLine($"MFT Start LCN: {header.MftStartLcn}");
            k_StringBuilder.AppendLine($"MFT2 Start LCN: {header.Mft2StartLcn}");
            k_StringBuilder.AppendLine($"MFT Zone Start: {header.MftZoneStart}");
            k_StringBuilder.AppendLine($"MFT Zone End: {header.MftZoneEnd}");
            k_StringBuilder.AppendLine($"Byte Count: {data.ByteCount}");
            k_StringBuilder.AppendLine($"Major Version: {data.MajorVersion}");
            k_StringBuilder.AppendLine($"Minor Version: {data.MinorVersion}");

            return k_StringBuilder.ToString();
        }
    }
}
