using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming

namespace MFTLib;

/// <summary>
/// Fixed-layout header for an MFT file record
/// https://flatcap.github.io/linux-ntfs/ntfs/concepts/file_record.html
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FileRecordHeader
{
    public const uint kMagicNumber = 0x454C4946; // "FILE"
    /// <summary>
    /// 4-byte magic number "FILE"
    /// </summary>
    public uint magic;

    /// <summary>
    /// Offset to the Update Sequence Array (in bytes)
    /// </summary>
    public ushort updateSequenceOffset;

    /// <summary>
    /// Size of the Update Sequence (in words)
    /// </summary>
    public ushort updateSequenceSize;

    /// <summary>
    /// $LogFile sequence number (LSN)
    /// </summary>
    public ulong logSequence;

    /// <summary>
    /// Sequence number
    /// </summary>
    public ushort sequenceNumber;

    /// <summary>
    /// Hard link count
    /// </summary>
    public ushort hardLinkCount;

    /// <summary>
    /// Offset to the first Attribute (in bytes)
    /// </summary>
    public ushort firstAttributeOffset;

    public ushort flags;

    /// <summary>
    /// Real size of the FILE record (in bytes)
    /// </summary>
    public uint usedSize;

    /// <summary>
    /// Allocated size of the FILE record (in bytes)
    /// </summary>
    public uint allocatedSize;

    /// <summary>
    /// File reference to the base FILE record
    /// </summary>
    public ulong fileReference;

    /// <summary>
    /// Next Attribute Id
    /// </summary>
    public ushort nextAttributeID;

    /// <summary>
    /// unused
    /// </summary>
    public ushort unused;

    /// <summary>
    /// Number of this MFT Record
    /// </summary>
    public uint recordNumber;

    /// <summary>
    /// Indicates if the file record is in use
    /// </summary>
    public bool inUse
    {
        get => (flags & 0x0001) != 0;
        set
        {
            if (value)
                flags |= 0x0001;
            else
                flags &= 0xFFFE;
        }
    }

    /// <summary>
    /// Indicates if the file record is a directory
    /// </summary>
    public bool isDirectory
    {
        get => (flags & 0x0002) != 0;
        set
        {
            if (value)
                flags |= 0x0002;
            else
                flags &= 0xFFFD;
        }
    }
}

/// <summary>
/// Standard attribute header
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct AttributeHeader
{
    public AttributeType attributeType;
    public uint length;
    public byte nonResident;
    public byte nameLength;
    public ushort nameOffset;
    public ushort flags;
    public ushort attributeID;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct ResidentAttributeHeader
{
    public AttributeHeader standard;
    public uint attributeLength;
    public ushort attributeOffset;
    public byte indexed;
    public byte unused;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct NonResidentAttributeHeader
{
    public AttributeHeader standard;
    public ulong firstCluster;
    public ulong lastCluster;
    public ushort dataRunsOffset;
    public ushort compressionUnit;
    public uint unused;
    public ulong attributeAllocated;
    public ulong attributeSize;
    public ulong streamDataSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
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

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct FileNameAttributeHeader
{
    public ResidentAttributeHeader resident;
    public ulong parentRecordNumberAndSequenceNumber;
    public ulong creationTime;
    public ulong modificationTime;
    public ulong metadataModificationTime;
    public ulong readTime;
    public ulong allocatedSize;
    public ulong realSize;
    public uint flags;
    public uint repase;
    public byte fileNameLength;
    public byte namespaceType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public byte[] FileName;

    // Property to get/set the parent record number (48 bits)
    public ulong parentRecordNumber
    {
        get => parentRecordNumberAndSequenceNumber & 0x0000FFFFFFFFFFFF;
        set => parentRecordNumberAndSequenceNumber = (parentRecordNumberAndSequenceNumber & 0xFFFF000000000000) | (value & 0x0000FFFFFFFFFFFF);
    }

    // Property to get/set the sequence number (16 bits)
    public ushort sequenceNumber
    {
        get => (ushort)((parentRecordNumberAndSequenceNumber >> 48) & 0xFFFF);
        set => parentRecordNumberAndSequenceNumber = (parentRecordNumberAndSequenceNumber & 0x0000FFFFFFFFFFFF) | ((ulong)value << 48);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct ObjectIdAttribute
{
    public Guid ObjectId;
    public Guid BirthVolumeId;
    public Guid BirthObjectId;
    public Guid DomainId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct BootSector
{
    public fixed byte jump[3];

    // Defined as char[8] in the C++ code
    public fixed byte name[8];
    public ushort bytesPerSector;
    public byte sectorsPerCluster;
    public ushort reservedSectors;
    public fixed byte unused0[3];
    public ushort unused1;
    public byte media;
    public ushort unused2;
    public ushort sectorsPerTrack;
    public ushort headsPerCylinder;
    public uint hiddenSectors;
    public uint unused3;
    public uint unused4;
    public ulong totalSectors;
    public ulong mftStart;
    public ulong mftMirrorStart;
    public uint clustersPerFileRecord;
    public uint clustersPerIndexBlock;
    public ulong serialNumber;
    public uint checksum;
    public fixed byte bootloader[426];
    public ushort bootSignature;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct RunHeader
{
    private byte _data;

    public byte lengthFieldBytes
    {
        get => (byte)(_data & 0x0F);
        set => _data = (byte)((_data & 0xF0) | (value & 0x0F));
    }

    public byte offsetFieldBytes
    {
        get => (byte)((_data >> 4) & 0x0F);
        set => _data = (byte)((_data & 0x0F) | ((value & 0x0F) << 4));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
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

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NTFS_EXTENDED_VOLUME_DATA_BUFFER
{
    public NTFS_VOLUME_DATA_BUFFER VolumeData;
    public ulong ByteCount;
    public ushort MajorVersion;
    public ushort MinorVersion;
}
