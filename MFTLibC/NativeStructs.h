#pragma once

#include <cstdint>
#include <array>

// Define the NTFS file record header
struct FileRecordHeader {
    std::array<char, 4> MagicNumber; // "FILE"
    uint16_t UpdateSequenceOffset;
    uint16_t UpdateSequenceSize;
    uint64_t LogFileSequenceNumber;
    uint16_t SequenceNumber;
    uint16_t HardLinkCount;
    uint16_t FirstAttributeOffset;
    uint16_t Flags;
    uint32_t RealSize;
    uint32_t AllocatedSize;
    uint64_t BaseFileRecord;
    uint16_t NextAttributeId;
    uint16_t Padding;
    uint32_t MftRecordNumber;
    std::array<uint16_t, 1> UpdateSequenceArray; // Placeholder for the actual size
};

// Define the attribute header
struct AttributeHeader {
    uint32_t Type;
    uint32_t Length;
    uint8_t NonResidentFlag;
    uint8_t NameLength;
    uint16_t NameOffset;
    uint16_t Flags;
    uint16_t AttributeId;
    uint32_t AttributeLength;
    uint16_t AttributeOffset;
};

// Define the standard information attribute
struct StandardInformationAttribute {
    uint64_t CreationTime;
    uint64_t ChangeTime;
    uint64_t LastWriteTime;
    uint64_t LastAccessTime;
    uint32_t FileAttributes;
    uint32_t MaximumVersions;
    uint32_t VersionNumber;
    uint32_t ClassId;
    uint32_t OwnerId;
    uint32_t SecurityId;
    uint64_t QuotaCharged;
    uint64_t UpdateSequenceNumber;
};

// Define the file name attribute
struct FileNameAttribute {
    uint64_t ParentDirectory;
    uint64_t CreationTime;
    uint64_t ChangeTime;
    uint64_t LastWriteTime;
    uint64_t LastAccessTime;
    uint64_t AllocatedSize;
    uint64_t RealSize;
    uint32_t Flags;
    uint32_t EaSize;
    uint8_t FileNameLength;
    uint8_t FileNameNamespace;
    std::array<uint8_t, 1> FileName; // Placeholder for the actual size
};

struct ObjectIdAttribute
{
    GUID ObjectId;
    GUID BirthVolumeId;
    GUID BirthObjectId;
    GUID DomainId;
};

enum AttributeType : uint32_t
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