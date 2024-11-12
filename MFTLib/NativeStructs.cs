using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming

namespace MFTLib;

[StructLayout(LayoutKind.Sequential)]
public struct NTFS_VOLUME_DATA_BUFFER
{
    public ulong VolumeSerialNumber;
    public ulong NumberSectors;
    public ulong TotalClusters;
    public ulong FreeClusters;
    public ulong TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public ulong MftValidDataLength;
    public ulong MftStartLcn;
    public ulong Mft2StartLcn;
    public ulong MftZoneStart;
    public ulong MftZoneEnd;
}

[StructLayout(LayoutKind.Sequential)]
public struct NTFS_FILE_RECORD_INPUT_BUFFER
{
    public ulong FileReferenceNumber;
}

[StructLayout(LayoutKind.Sequential)]
public struct NTFS_FILE_RECORD_OUTPUT_BUFFER
{
    public ulong FileReferenceNumber;
    public int FileRecordLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public byte[] FileRecordBuffer;
}

/// <summary>
/// Fixed-layout header for an MFT file record
/// https://flatcap.github.io/linux-ntfs/ntfs/concepts/file_record.html
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FileRecordHeader
{
    /// <summary>
    /// 4-byte magic number "FILE"
    /// </summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public char[] MagicNumber;

    /// <summary>
    /// Offset to the Update Sequence Array (in bytes)
    /// </summary>
    public ushort UpdateSequenceOffset;

    /// <summary>
    /// Size of the Update Sequence (in words)
    /// </summary>
    public ushort UpdateSequenceSize;

    /// <summary>
    /// $LogFile sequence number (LSN)
    /// </summary>
    public ulong LogFileSequenceNumber;

    /// <summary>
    /// Sequence number
    /// </summary>
    public ushort SequenceNumber;

    /// <summary>
    /// Hard link count
    /// </summary>
    public ushort HardLinkCount;

    /// <summary>
    /// Offset to the first Attribute (in bytes)
    /// </summary>
    public ushort FirstAttributeOffset;

    /// <summary>
    /// Flags (e.g., in use, directory)
    /// </summary>
    public ushort Flags;

    /// <summary>
    /// Real size of the FILE record (in bytes)
    /// </summary>
    public uint RealSize;

    /// <summary>
    /// Allocated size of the FILE record (in bytes)
    /// </summary>
    public uint AllocatedSize;

    /// <summary>
    /// File reference to the base FILE record
    /// </summary>
    public ulong BaseFileRecord;

    /// <summary>
    /// Next Attribute Id
    /// </summary>
    public ushort NextAttributeId;

    /// <summary>
    /// Padding
    /// </summary>
    public ushort Padding;

    /// <summary>
    /// Number of this MFT Record
    /// </summary>
    public uint MftRecordNumber;

    /// <summary>
    /// Update Sequence Array
    /// </summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public ushort[] UpdateSequenceArray;
}

/// <summary>
/// Standard attribute header
/// TODO: Check resident vs. non-resident attributes
/// TODO: Check name vs no-name
/// Assume resident, no-name for now
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct AttributeHeader
{
    public AttributeType Type;
    public uint Length;
    public byte NonResidentFlag;
    public byte NameLength;
    public ushort NameOffset;
    public ushort Flags;
    public ushort AttributeId;
    public uint AttributeLength;
    public ushort AttributeOffset;
}

[StructLayout(LayoutKind.Sequential)]
struct StandardInformationAttribute
{
    public ulong CreationTime;
    public ulong ChangeTime;
    public ulong LastWriteTime;
    public ulong LastAccessTime;
    public uint FileAttributes;
    public uint MaximumVersions;
    public uint VersionNumber;
    public uint ClassId;
    public uint OwnerId;
    public uint SecurityId;
    public ulong QuotaCharged;
    public ulong UpdateSequenceNumber;
}

[StructLayout(LayoutKind.Sequential)]
struct FileNameAttribute
{
    public ulong ParentDirectory;
    public ulong CreationTime;
    public ulong ChangeTime;
    public ulong LastWriteTime;
    public ulong LastAccessTime;
    public ulong AllocatedSize;
    public ulong RealSize;
    public uint Flags;
    public uint EaSize;
    public byte FileNameLength;
    public byte FileNameNamespace;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public byte[] FileName;
}

[StructLayout(LayoutKind.Sequential)]
struct ObjectIdAttribute
{
    public Guid ObjectId;
    public Guid BirthVolumeId;
    public Guid BirthObjectId;
    public Guid DomainId;
}
