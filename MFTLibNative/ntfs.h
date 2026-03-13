#pragma once

#pragma pack(push, 1)
typedef LARGE_INTEGER VCN, *PVCN;
typedef USHORT UPDATE_SEQUENCE_NUMBER, *PUPDATE_SEQUENCE_NUMBER;
typedef UPDATE_SEQUENCE_NUMBER UPDATE_SEQUENCE_ARRAY[1];

#define FILE_RECORD_SIZE (1024)

enum ATTRIBUTE_TYPE_CODE : uint32_t
{
    StandardInformation = 0x10,
    AttributeList = 0x20,
    FileName = 0x30,
    ObjectId = 0x40,
    SecurityDescriptor = 0x50,
    VolumeName = 0x60,
    VolumeInformation = 0x70,
    Data = 0x80,
    IndexRoot = 0x90,
    IndexAllocation = 0xA0,
    Bitmap = 0xB0,
    ReparsePoint = 0xC0,
    EAInformation = 0xD0,
    EA = 0xE0,
    PropertySet = 0xF0,
    LoggedUtilityStream = 0x100,
    EndMarker = 0xFFFFFFFF
};

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/mft-segment-reference
typedef struct _MFT_SEGMENT_REFERENCE {
    ULONG  SegmentNumberLowPart;
    USHORT SegmentNumberHighPart;
    USHORT SequenceNumber;
} MFT_SEGMENT_REFERENCE, * PMFT_SEGMENT_REFERENCE;

typedef _MFT_SEGMENT_REFERENCE FILE_REFERENCE, *PFILE_REFERENCE;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header
typedef struct _ATTRIBUTE_RECORD_HEADER {
    ATTRIBUTE_TYPE_CODE TypeCode;
    ULONG               RecordLength;
    UCHAR               FormCode;
    UCHAR               NameLength;
    USHORT              NameOffset;
    USHORT              Flags;
    USHORT              Instance;
    union {
        struct {
            ULONG  ValueLength;
            USHORT ValueOffset;
            UCHAR  Reserved[2];
        } Resident;
        struct {
            VCN      LowestVcn;
            VCN      HighestVcn;
            USHORT   MappingPairsOffset;
            UCHAR    Reserved[6];
            LONGLONG AllocatedLength;
            LONGLONG FileSize;
            LONGLONG ValidDataLength;
            LONGLONG TotalAllocated;
        } Nonresident;
    } Form;
} ATTRIBUTE_RECORD_HEADER, * PATTRIBUTE_RECORD_HEADER;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-list-entry
typedef struct _ATTRIBUTE_LIST_ENTRY {
    ATTRIBUTE_TYPE_CODE   AttributeTypeCode;
    USHORT                RecordLength;
    UCHAR                 AttributeNameLength;
    UCHAR                 AttributeNameOffset;
    VCN                   LowestVcn;
    MFT_SEGMENT_REFERENCE SegmentReference;
    USHORT                Reserved;
    WCHAR                 AttributeName[1];
} ATTRIBUTE_LIST_ENTRY, * PATTRIBUTE_LIST_ENTRY;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/file-name
typedef struct _FILE_NAME {
    FILE_REFERENCE ParentDirectory;
    UCHAR          Reserved[0x38];
    UCHAR          FileNameLength;
    UCHAR          Flags;
    WCHAR          FileName[1];
} FILE_NAME, * PFILE_NAME;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header
typedef struct _MULTI_SECTOR_HEADER {
    union
    {
        UCHAR  Signature[4];
        UINT Magic;
    };
    USHORT UpdateSequenceArrayOffset;
    USHORT UpdateSequenceArraySize;
} MULTI_SECTOR_HEADER, * PMULTI_SECTOR_HEADER;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header
typedef struct _FILE_RECORD_SEGMENT_HEADER {
    MULTI_SECTOR_HEADER   MultiSectorHeader;
    ULONGLONG             Reserved1;
    USHORT                SequenceNumber;
    USHORT                Reserved2;
    USHORT                FirstAttributeOffset;
    USHORT                Flags;
    ULONG                 Reserved3[2];
    FILE_REFERENCE        BaseFileRecordSegment;
    USHORT                Reserved4;
    UPDATE_SEQUENCE_ARRAY UpdateSequenceArray;
} FILE_RECORD_SEGMENT_HEADER, * PFILE_RECORD_SEGMENT_HEADER;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/standard-information
typedef struct _STANDARD_INFORMATION {
    UCHAR Reserved[0x30];
    ULONG OwnerId;
    ULONG SecurityId;
} STANDARD_INFORMATION, * PSTANDARD_INFORMATION;

struct StandardInformationAttribute : ATTRIBUTE_RECORD_HEADER {
    uint64_t    creationTime;
    uint64_t    modificationTime;
    uint64_t    metadataModificationTime;
    uint64_t    readTime;
    uint32_t    permissions;
    uint32_t    maxVersions;
    uint32_t    version;
    uint32_t    classId;
    uint32_t    ownerId;
    uint32_t    securityId;
    uint64_t    quota;
    uint64_t    updateSequence;
};

struct RunHeader {
    uint8_t lengthFieldBytes : 4;
    uint8_t offsetFieldBytes : 4;
};

struct NTFS_COMBINED_VOLUME_DATA
{
    NTFS_VOLUME_DATA_BUFFER StandardData;
    NTFS_EXTENDED_VOLUME_DATA ExtendedData;
};

// BIOS parameter block -- first cluster of the hard drive which contains filesystem info
struct NTFS_BPB {
    uint8_t     jump[3];
    char        name[8];
    uint16_t    bytesPerSector;
    uint8_t     sectorsPerCluster;
    uint16_t    reservedSectors;
    uint8_t     unused0[3];
    uint16_t    unused1;
    uint8_t     media;
    uint16_t    unused2;
    uint16_t    sectorsPerTrack;
    uint16_t    headsPerCylinder;
    uint32_t    hiddenSectors;
    uint32_t    unused3;
    uint32_t    unused4;
    uint64_t    totalSectors;
    uint64_t    mftStart;
    uint64_t    mftMirrorStart;
    uint32_t    clustersPerFileRecord;
    uint32_t    clustersPerIndexBlock;
    uint64_t    serialNumber;
    uint32_t    checksum;
    uint8_t     bootloader[426];
    uint16_t    bootSignature;
};
#pragma pack(pop)
