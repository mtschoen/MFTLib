using System.Runtime.InteropServices;

namespace MFTLib.Interop;

// Mirrors the Win32 NTFS_VOLUME_DATA_BUFFER layout returned by
// FSCTL_GET_NTFS_VOLUME_DATA (winioctl.h). Field order and sizes are load-bearing: the
// kernel writes this exact 96-byte layout, and only NtfsVolumeInformation.Query decodes
// it. Fields this codebase does not otherwise use are still declared so the struct's
// total size and later field offsets match the kernel's layout.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct NtfsVolumeDataBufferNative
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}
