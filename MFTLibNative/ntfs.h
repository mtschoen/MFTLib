#pragma once

#pragma pack(push, 1)
typedef LARGE_INTEGER VCN, *PVCN;
typedef USHORT UPDATE_SEQUENCE_NUMBER, *PUPDATE_SEQUENCE_NUMBER;
typedef UPDATE_SEQUENCE_NUMBER UPDATE_SEQUENCE_ARRAY[1];

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

typedef struct _MFT_SEGMENT_REFERENCE {
    ULONG  SegmentNumberLowPart;
    USHORT SegmentNumberHighPart;
    USHORT SequenceNumber;
} MFT_SEGMENT_REFERENCE, * PMFT_SEGMENT_REFERENCE;

typedef _MFT_SEGMENT_REFERENCE FILE_REFERENCE, *PFILE_REFERENCE;

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

typedef struct _FILE_NAME {
    FILE_REFERENCE ParentDirectory;
    UCHAR          Reserved[0x38];
    UCHAR          FileNameLength;
    UCHAR          Flags;
    WCHAR          FileName[1];
} FILE_NAME, * PFILE_NAME;

typedef struct _MULTI_SECTOR_HEADER {
    UCHAR  Signature[4];
    USHORT UpdateSequenceArrayOffset;
    USHORT UpdateSequenceArraySize;
} MULTI_SECTOR_HEADER, * PMULTI_SECTOR_HEADER;

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

typedef struct _STANDARD_INFORMATION {
    UCHAR Reserved[0x30];
    ULONG OwnerId;
    ULONG SecurityId;
} STANDARD_INFORMATION, * PSTANDARD_INFORMATION;

struct NTFS_COMBINED_VOLUME_DATA
{
    NTFS_VOLUME_DATA_BUFFER StandardData;
    NTFS_EXTENDED_VOLUME_DATA ExtendedData;
};
#pragma pack(pop)
