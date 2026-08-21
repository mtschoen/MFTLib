#pragma once

#include <cstdint>

// These types mirror the on-disk NTFS layout and the Microsoft-documented
// structures (see learn.microsoft.com links below). They are the C ABI / binary
// surface: several use trailing flexible-array members (e.g. WCHAR FileName[1])
// read past [0] for variable-length on-disk data, which std::array cannot model.
// Wrapping them in extern "C" both states that intent and makes clang-tidy skip
// modernize-avoid-c-arrays here (it ignores extern "C" shared-header code).
extern "C" {
#ifndef _WIN32
using ULONG = uint32_t;
using USHORT = uint16_t;
using UCHAR = uint8_t;
using LONGLONG = int64_t;
using ULONGLONG = uint64_t;
union LARGE_INTEGER {
    int64_t QuadPart = 0;
    struct {
        uint32_t LowPart;
        int32_t HighPart;
    };
};
using PVOID = void*;
using BOOL = int;
using DWORD = uint32_t;
using HANDLE = void*;
using PDWORD = DWORD*;
using UINT = unsigned int;
using WCHAR = char16_t;
#endif

#pragma pack(push, 1)
using VCN = LARGE_INTEGER;
using PVCN = VCN*;
using UPDATE_SEQUENCE_NUMBER = USHORT;
using PUPDATE_SEQUENCE_NUMBER = UPDATE_SEQUENCE_NUMBER*;
// NOLINTNEXTLINE(modernize-avoid-c-arrays): on-disk NTFS update-sequence-array layout type
using UPDATE_SEQUENCE_ARRAY = UPDATE_SEQUENCE_NUMBER[1];

constexpr uint32_t DEFAULT_FILE_RECORD_SIZE = 1024;
constexpr uint32_t MIN_FILE_RECORD_SIZE = 512;
constexpr uint32_t MAX_FILE_RECORD_SIZE = 65536;

struct ParseGeometry {
    uint32_t recordSize = 0;
};

inline bool IsSupportedRecordSize(uint32_t value) {
    return value >= MIN_FILE_RECORD_SIZE && value <= MAX_FILE_RECORD_SIZE && (value % 512U) == 0U &&
           (value & (value - 1U)) == 0U;
}

enum ATTRIBUTE_TYPE_CODE : uint32_t {
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
using MFT_SEGMENT_REFERENCE = struct MftSegmentReferenceLayout {
    ULONG SegmentNumberLowPart = 0;
    USHORT SegmentNumberHighPart = 0;
    USHORT SequenceNumber = 0;
};
using PMFT_SEGMENT_REFERENCE = MFT_SEGMENT_REFERENCE*;

using FILE_REFERENCE = MftSegmentReferenceLayout;
using PFILE_REFERENCE = FILE_REFERENCE*;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header
using ATTRIBUTE_RECORD_HEADER = struct AttributeRecordHeaderLayout {
    ATTRIBUTE_TYPE_CODE TypeCode = StandardInformation;
    ULONG RecordLength = 0;
    UCHAR FormCode = 0;
    UCHAR NameLength = 0;
    USHORT NameOffset = 0;
    USHORT Flags = 0;
    USHORT Instance = 0;

    union {
        struct {
            ULONG ValueLength;
            USHORT ValueOffset;
            UCHAR Reserved[2];
        } Resident;

        struct {
            VCN LowestVcn;
            VCN HighestVcn;
            USHORT MappingPairsOffset;
            UCHAR Reserved[6];
            LONGLONG AllocatedLength;
            LONGLONG FileSize;
            LONGLONG ValidDataLength;
            LONGLONG TotalAllocated;
        } Nonresident;
    } Form{};
};
using PATTRIBUTE_RECORD_HEADER = ATTRIBUTE_RECORD_HEADER*;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-list-entry
using ATTRIBUTE_LIST_ENTRY = struct AttributeListEntryLayout {
    ATTRIBUTE_TYPE_CODE AttributeTypeCode = StandardInformation;
    USHORT RecordLength = 0;
    UCHAR AttributeNameLength = 0;
    UCHAR AttributeNameOffset = 0;
    VCN LowestVcn{};
    MFT_SEGMENT_REFERENCE SegmentReference{};
    USHORT Reserved = 0;
    WCHAR AttributeName[1]{};
};
using PATTRIBUTE_LIST_ENTRY = ATTRIBUTE_LIST_ENTRY*;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/file-name
using FILE_NAME = struct FileNameLayout {
    FILE_REFERENCE ParentDirectory{};
    uint64_t CreationTime = 0;
    uint64_t ModificationTime = 0;
    uint64_t MftModificationTime = 0;
    uint64_t ReadTime = 0;
    uint64_t AllocatedSize = 0;
    uint64_t FileSize = 0;
    uint32_t FileAttributes = 0;   // e.g. FILE_ATTRIBUTE_DIRECTORY
    uint32_t ReparsePointTag = 0;  // or EA size
    UCHAR FileNameLength = 0;
    UCHAR Flags = 0;
    WCHAR FileName[1]{};
};
using PFILE_NAME = FILE_NAME*;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header
using MULTI_SECTOR_HEADER = struct MultiSectorHeaderLayout {
    union {
        UINT Magic = 0;
        UCHAR Signature[4];
    };

    USHORT UpdateSequenceArrayOffset = 0;
    USHORT UpdateSequenceArraySize = 0;
};
using PMULTI_SECTOR_HEADER = MULTI_SECTOR_HEADER*;

// from https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header
using FILE_RECORD_SEGMENT_HEADER = struct FileRecordSegmentHeaderLayout {
    MULTI_SECTOR_HEADER MultiSectorHeader{};
    ULONGLONG Reserved1 = 0;
    USHORT SequenceNumber = 0;
    USHORT Reserved2 = 0;
    USHORT FirstAttributeOffset = 0;
    USHORT Flags = 0;
    ULONG Reserved3[2] = {0, 0};
    FILE_REFERENCE BaseFileRecordSegment{};
    USHORT Reserved4 = 0;
    UPDATE_SEQUENCE_ARRAY UpdateSequenceArray{};
};
using PFILE_RECORD_SEGMENT_HEADER = FILE_RECORD_SEGMENT_HEADER*;

struct RunHeader {
    uint8_t lengthFieldBytes : 4;
    uint8_t offsetFieldBytes : 4;
};

// BIOS parameter block -- first cluster of the hard drive which contains filesystem info
struct NTFS_BPB {
    uint8_t jump[3];
    char name[8];
    uint16_t bytesPerSector;
    uint8_t sectorsPerCluster;
    uint16_t reservedSectors;
    uint8_t unused0[3];
    uint16_t unused1;
    uint8_t media;
    uint16_t unused2;
    uint16_t sectorsPerTrack;
    uint16_t headsPerCylinder;
    uint32_t hiddenSectors;
    uint32_t unused3;
    uint32_t unused4;
    uint64_t totalSectors;
    uint64_t mftStart;
    uint64_t mftMirrorStart;
    uint32_t clustersPerFileRecord;
    uint32_t clustersPerIndexBlock;
    uint64_t serialNumber;
    uint32_t checksum;
    uint8_t bootloader[426];
    uint16_t bootSignature;
};
#pragma pack(pop)
}  // extern "C"
