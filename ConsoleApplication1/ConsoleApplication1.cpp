/*
    This is free and unencumbered software released into the public domain.
    Anyone is free to copy, modify, publish, use, compile, sell, or distribute this
    software, either in source code form or as a compiled binary, for any purpose,
    commercial or non-commercial, and by any means.
    In jurisdictions that recognize copyright laws, the author or authors of this
    software dedicate any and all copyright interest in the software to the public
    domain. We make this dedication for the benefit of the public at large and to
    the detriment of our heirs and successors. We intend this dedication to be an
    overt act of relinquishment in perpetuity of all present and future rights to
    this software under copyright law.
    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
    ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
    WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

#include <stdio.h>
#include <assert.h>
#include <stdint.h>
#include <windows.h>

#define STB_DS_IMPLEMENTATION
#include "stb_ds.h"

#pragma pack(push,1)
struct BootSector {
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

struct FileRecordHeader {
    uint32_t    magic;
    uint16_t    updateSequenceOffset;
    uint16_t    updateSequenceSize;
    uint64_t    logSequence;
    uint16_t    sequenceNumber;
    uint16_t    hardLinkCount;
    uint16_t    firstAttributeOffset;
    uint16_t    inUse : 1;
    uint16_t    isDirectory : 1;
    uint32_t    usedSize;
    uint32_t    allocatedSize;
    uint64_t    fileReference;
    uint16_t    nextAttributeID;
    uint16_t    unused;
    uint32_t    recordNumber;
};

// TODO: https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header
struct AttributeHeader {
    uint32_t    attributeType;
    uint32_t    length;
    uint8_t     nonResident;
    uint8_t     nameLength;
    uint16_t    nameOffset;
    uint16_t    flags;
    uint16_t    attributeID;
};

struct ResidentAttributeHeader : AttributeHeader {
    uint32_t    attributeLength;
    uint16_t    attributeOffset;
    uint8_t     indexed;
    uint8_t     unused;
};

struct NonResidentAttributeHeader : AttributeHeader {
    uint64_t    firstCluster;
    uint64_t    lastCluster;
    uint16_t    dataRunsOffset;
    uint16_t    compressionUnit;
    uint32_t    unused;
    uint64_t    attributeAllocated;
    uint64_t    attributeSize;
    uint64_t    streamDataSize;
};

struct StandardInformationAttributeNonResident : NonResidentAttributeHeader {
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

struct StandardInformationAttributeResident : ResidentAttributeHeader {
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

static uint64_t ExtractParentRecordNumber(uint64_t value) {
    return value & 0xFFFFFFFFFFFF; // Mask to get the lower 48 bits
}

static uint16_t ExtractSequenceNumber(uint64_t value) {
    return (value >> 48) & 0xFFFF; // Shift right by 48 bits and mask to get the upper 16 bits
}

struct FileNameAttributeHeaderNonResident : NonResidentAttributeHeader {
    uint64_t    parentRecordNumber : 48;
    uint64_t    sequenceNumber : 16;
    uint64_t    creationTime;
    uint64_t    modificationTime;
    uint64_t    metadataModificationTime;
    uint64_t    readTime;
    uint64_t    allocatedSize;
    uint64_t    realSize;
    uint32_t    filename_flags;
    uint32_t    repase;
    uint8_t     fileNameLength;
    uint8_t     namespaceType;
    wchar_t     fileName[1];
};

struct FileNameAttributeHeaderResident : ResidentAttributeHeader {
    uint64_t    parentRecordNumber : 48;
    uint64_t    sequenceNumber : 16;
    uint64_t    creationTime;
    uint64_t    modificationTime;
    uint64_t    metadataModificationTime;
    uint64_t    readTime;
    uint64_t    allocatedSize;
    uint64_t    realSize;
    uint32_t    filename_flags;
    uint32_t    repase;
    uint8_t     fileNameLength;
    uint8_t     namespaceType;
    wchar_t     fileName[1];
};

// TODO: replace with https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-list-entry
struct AttributeListAttribute
{
	uint32_t    type;
	uint16_t    length;
	uint8_t     nameLength;
	uint8_t     nameOffset;
	uint64_t    startingVCN;
	uint64_t    baseFileReference;
	uint16_t    attributeID;
	uint8_t     name[1];
};

struct RunHeader {
    uint8_t     lengthFieldBytes : 4;
    uint8_t     offsetFieldBytes : 4;
};
#pragma pack(pop)

struct File {
    uint64_t    parent;
    char* name;
};

File* files;

DWORD bytesAccessed;
HANDLE drive;

BootSector bootSector;

#define MFT_FILE_SIZE (1024)
uint8_t mftFile[MFT_FILE_SIZE];

#define MFT_FILES_PER_BUFFER (65536)
uint8_t mftBuffer[MFT_FILES_PER_BUFFER * MFT_FILE_SIZE];

char* DuplicateName(wchar_t* name, size_t nameLength) {
    static char* allocationBlock = nullptr;
    static size_t bytesRemaining = 0;

    size_t bytesNeeded = WideCharToMultiByte(CP_UTF8, 0, name, nameLength, NULL, 0, NULL, NULL) + 1;

    if (bytesRemaining < bytesNeeded) {
        allocationBlock = (char*)malloc((bytesRemaining = 16 * 1024 * 1024));
    }

    char* buffer = allocationBlock;
    buffer[bytesNeeded - 1] = 0;
    WideCharToMultiByte(CP_UTF8, 0, name, nameLength, allocationBlock, bytesNeeded, NULL, NULL);

    bytesRemaining -= bytesNeeded;
    allocationBlock += bytesNeeded;

    return buffer;
}

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

void Read(void* buffer, uint64_t from, uint64_t count) {
    LONG high = from >> 32;
    SetFilePointer(drive, from & 0xFFFFFFFF, &high, FILE_BEGIN);
    ReadFile(drive, buffer, count, &bytesAccessed, NULL);
    assert(bytesAccessed == count);
}

static void PrintAttribute(AttributeHeader* attribute)
{
	fprintf(stdout, "  Type: %08X\n", attribute->attributeType);
	fprintf(stdout, "  Length: %u\n", attribute->length);
	fprintf(stdout, "  Non-resident: %u\n", attribute->nonResident);
	fprintf(stdout, "  Name length: %u\n", attribute->nameLength);
	fprintf(stdout, "  Name offset: %u\n", attribute->nameOffset);
	fprintf(stdout, "  Flags: %u\n", attribute->flags);
	fprintf(stdout, "  Attribute ID: %u\n", attribute->attributeID);
}

static void PrintResidentAttribute(ResidentAttributeHeader* attribute)
{
    fprintf(stdout, "  *Resident attribute\n");
    PrintAttribute(attribute);
	fprintf(stdout, "  Attribute length: %u\n", attribute->attributeLength);
	fprintf(stdout, "  Attribute offset: %u\n", attribute->attributeOffset);
	fprintf(stdout, "  Indexed: %u\n", attribute->indexed);
    fprintf(stdout, "  Unused: %u\n", attribute->unused);
}

static void PrintNonResidentAttribute(NonResidentAttributeHeader* attribute)
{
    fprintf(stdout, "  *Non-resident attribute\n");
    PrintAttribute(attribute);
	fprintf(stdout, "  First cluster: %llu\n", attribute->firstCluster);
	fprintf(stdout, "  Last cluster: %llu\n", attribute->lastCluster);
	fprintf(stdout, "  Data runs offset: %u\n", attribute->dataRunsOffset);
	fprintf(stdout, "  Compression unit: %u\n", attribute->compressionUnit);
	fprintf(stdout, "  Unused: %u\n", attribute->unused);
	fprintf(stdout, "  Attribute allocated: %llu\n", attribute->attributeAllocated);
	fprintf(stdout, "  Attribute size: %llu\n", attribute->attributeSize);
	fprintf(stdout, "  Stream data size: %llu\n", attribute->streamDataSize);
}

int main(int argc, char** argv) {
    drive = CreateFile(L"\\\\.\\G:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (drive == INVALID_HANDLE_VALUE) {
        fprintf(stderr, "Failed to open drive. Error: %lu\n", GetLastError());
        return 1;
    }

    Read(&bootSector, 0, 512);
    fprintf(stdout, "Boot Sector:\n");
	fprintf(stdout, "  Bytes per sector: %u\n", bootSector.bytesPerSector);
	fprintf(stdout, "  Sectors per cluster: %u\n", bootSector.sectorsPerCluster);
	fprintf(stdout, "  Reserved sectors: %u\n", bootSector.reservedSectors);
	fprintf(stdout, "  Media: %u\n", bootSector.media);
	fprintf(stdout, "  Sectors per track: %u\n", bootSector.sectorsPerTrack);
	fprintf(stdout, "  Heads per cylinder: %u\n", bootSector.headsPerCylinder);
	fprintf(stdout, "  Hidden sectors: %u\n", bootSector.hiddenSectors);
	fprintf(stdout, "  Total sectors: %llu\n", bootSector.totalSectors);
	fprintf(stdout, "  MFT start: %llu\n", bootSector.mftStart);
	fprintf(stdout, "  MFT mirror start: %llu\n", bootSector.mftMirrorStart);
	fprintf(stdout, "  Clusters per file record: %u\n", bootSector.clustersPerFileRecord);
	fprintf(stdout, "  Clusters per index block: %u\n", bootSector.clustersPerIndexBlock);
	fprintf(stdout, "  Serial number: %llu\n", bootSector.serialNumber);
	fprintf(stdout, "  Checksum: %u\n", bootSector.checksum);
	fprintf(stdout, "  Boot signature: %u\n", bootSector.bootSignature);

    uint64_t bytesPerCluster = bootSector.bytesPerSector * bootSector.sectorsPerCluster;

    Read(&mftFile, bootSector.mftStart * bytesPerCluster, MFT_FILE_SIZE);

    FileRecordHeader* fileRecord = (FileRecordHeader*)mftFile;
    AttributeHeader* attribute = (AttributeHeader*)(mftFile + fileRecord->firstAttributeOffset);
    NonResidentAttributeHeader* dataAttribute = nullptr;
    uint64_t approximateRecordCount = 0;
    assert(fileRecord->magic == 0x454C4946);

    fprintf(stdout, "  Update sequence offset: %u\n", fileRecord->updateSequenceOffset);
    fprintf(stdout, "  Update sequence size: %u\n", fileRecord->updateSequenceSize);
    fprintf(stdout, "  Log file sequence number: %llu\n", fileRecord->logSequence);
    fprintf(stdout, "  Sequence number: %u\n", fileRecord->sequenceNumber);
    fprintf(stdout, "  Hard link count: %u\n", fileRecord->hardLinkCount);
    fprintf(stdout, "  First attribute offset: %u\n", fileRecord->firstAttributeOffset);
    fprintf(stdout, "  Flags: %u\n", fileRecord->inUse);
    fprintf(stdout, "  Used size: %u\n", fileRecord->usedSize);
    fprintf(stdout, "  Allocated size: %u\n", fileRecord->allocatedSize);
    fprintf(stdout, "  File reference to base record: %llu\n", fileRecord->fileReference);
    fprintf(stdout, "  Next attribute ID: %u\n", fileRecord->nextAttributeID);
    fprintf(stdout, "  Unused: %u\n", fileRecord->unused);
    fprintf(stdout, "  Record number: %u\n", fileRecord->recordNumber);

    int notInUseCount = 0;

    while (true) {
        if (attribute->attributeType == StandardInformation) {
            fprintf(stdout, "StandardInformation Attribute:\n");
            if (attribute->nonResident) {
                auto standardInformation = (StandardInformationAttributeNonResident*)attribute;
                PrintNonResidentAttribute(standardInformation);
                fprintf(stdout, "  Creation time: %llu\n", standardInformation->creationTime);
                fprintf(stdout, "  Change time: %llu\n", standardInformation->modificationTime);
                fprintf(stdout, "  Last write time: %llu\n", standardInformation->metadataModificationTime);
                fprintf(stdout, "  Last access time: %llu\n", standardInformation->readTime);
                fprintf(stdout, "  Permissions: %08X\n", standardInformation->permissions);
                fprintf(stdout, "  Max versions: %u\n", standardInformation->maxVersions);
                fprintf(stdout, "  Version number: %u\n", standardInformation->version);
                fprintf(stdout, "  Class ID: %u\n", standardInformation->classId);
                fprintf(stdout, "  Owner ID: %u\n", standardInformation->ownerId);
                fprintf(stdout, "  Security ID: %u\n", standardInformation->securityId);
                fprintf(stdout, "  Quota charged: %llu\n", standardInformation->quota);
                fprintf(stdout, "  Update sequence number: %llu\n", standardInformation->updateSequence);
            }
            else {
                auto standardInformation = (StandardInformationAttributeResident*)attribute;
                PrintResidentAttribute(standardInformation);
                fprintf(stdout, "  Creation time: %llu\n", standardInformation->creationTime);
                fprintf(stdout, "  Change time: %llu\n", standardInformation->modificationTime);
                fprintf(stdout, "  Last write time: %llu\n", standardInformation->metadataModificationTime);
                fprintf(stdout, "  Last access time: %llu\n", standardInformation->readTime);
                fprintf(stdout, "  Permissions: %08X\n", standardInformation->permissions);
                fprintf(stdout, "  Max versions: %u\n", standardInformation->maxVersions);
                fprintf(stdout, "  Version number: %u\n", standardInformation->version);
                fprintf(stdout, "  Class ID: %u\n", standardInformation->classId);
                fprintf(stdout, "  Owner ID: %u\n", standardInformation->ownerId);
                fprintf(stdout, "  Security ID: %u\n", standardInformation->securityId);
                fprintf(stdout, "  Quota charged: %llu\n", standardInformation->quota);
                fprintf(stdout, "  Update sequence number: %llu\n", standardInformation->updateSequence);
            }
        }
        else if (attribute->attributeType == AttributeList) {
            fprintf(stdout, "AttributeList Attribute:\n");
            if (attribute->nonResident) {
                const auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);

                RunHeader* dataRun = (RunHeader*)((uint8_t*)nonResidentAttribute + nonResidentAttribute->dataRunsOffset);
                uint64_t clusterNumber = 0;
                uint64_t externalFileReference = 0;

                while (((uint8_t*)dataRun - (uint8_t*)attribute) < attribute->length && dataRun->lengthFieldBytes) {
                    uint64_t length = 0, offset = 0;

                    for (int i = 0; i < dataRun->lengthFieldBytes; i++) {
                        length |= (uint64_t)(((uint8_t*)dataRun)[1 + i]) << (i * 8);
                    }

                    for (int i = 0; i < dataRun->offsetFieldBytes; i++) {
                        offset |= (uint64_t)(((uint8_t*)dataRun)[1 + dataRun->lengthFieldBytes + i]) << (i * 8);
                    }

                    if (offset & ((uint64_t)1 << (dataRun->offsetFieldBytes * 8 - 1))) {
                        for (int i = dataRun->offsetFieldBytes; i < 8; i++) {
                            offset |= (uint64_t)0xFF << (i * 8);
                        }
                    }

                    clusterNumber += offset;
                    dataRun = (RunHeader*)((uint8_t*)dataRun + 1 + dataRun->lengthFieldBytes + dataRun->offsetFieldBytes);

                    uint64_t attributeOffset = 0;
                    auto clusters = length / bytesPerCluster;
                    if (length % bytesPerCluster) {
                        clusters++;
                    }

                    uint8_t* attributeListBuffer = (uint8_t*)malloc(clusters * bytesPerCluster);

                    Read(attributeListBuffer, clusterNumber * bytesPerCluster, clusters * bytesPerCluster);

                    // TODO: read >1 attribute at a time
                    while (attributeOffset < length) {
                        auto attributeListAttribute = (AttributeListAttribute*)(attributeListBuffer + attributeOffset);
                        attributeOffset += attributeListAttribute->length;

                        fprintf(stdout, "  type: %08X\n", attributeListAttribute->type);
                        fprintf(stdout, "  length: %u\n", attributeListAttribute->length);
                        fprintf(stdout, "  name length: %u\n", attributeListAttribute->nameLength);
                        fprintf(stdout, "  name offset: %u\n", attributeListAttribute->nameOffset);
                        fprintf(stdout, "  starting VCN: %llu\n", attributeListAttribute->startingVCN);
                        fprintf(stdout, "  base file reference: %llu\n", attributeListAttribute->baseFileReference);
                        fprintf(stdout, "  attribute ID: %u\n", attributeListAttribute->attributeID);
                        assert(attributeListAttribute->length > 0);

                        // Just use the last one--it all seems to be the same
						externalFileReference = attributeListAttribute->baseFileReference;
                    }

                    free(attributeListBuffer);
                }

				assert(externalFileReference != 0);
				auto inputBuffer = NTFS_FILE_RECORD_INPUT_BUFFER();
                inputBuffer.FileReferenceNumber.QuadPart = (LONGLONG)externalFileReference;
				auto outputBuffer = NTFS_FILE_RECORD_OUTPUT_BUFFER();
                const DWORD outputBufferSize = 1036; // sizeof(NTFS_FILE_RECORD_OUTPUT_BUFFER) + MFT_FILE_SIZE;
                DWORD bytesReturned = 0;
                if (!DeviceIoControl(drive, FSCTL_GET_NTFS_FILE_RECORD, &inputBuffer, sizeof(NTFS_FILE_RECORD_INPUT_BUFFER), &outputBuffer, outputBufferSize, &bytesReturned, nullptr))
                {
                    printf("Failed to get NTFS volume data. Error: %lu\n", GetLastError());
                    assert(false);
                }

                if (bytesReturned != outputBufferSize)
                {
                    printf("Failed to get NTFS volume data. Buffer sizes don't match. Error: %lu\n", GetLastError());
                    assert(false);
                }

				auto externalFileRecord = (FileRecordHeader*)outputBuffer.FileRecordBuffer;
				fprintf(stdout, "External file record:\n");
				fprintf(stdout, "  Update sequence offset: %u\n", externalFileRecord->updateSequenceOffset);
				fprintf(stdout, "  Update sequence size: %u\n", externalFileRecord->updateSequenceSize);
				fprintf(stdout, "  Log file sequence number: %llu\n", externalFileRecord->logSequence);
				fprintf(stdout, "  Sequence number: %u\n", externalFileRecord->sequenceNumber);
				fprintf(stdout, "  Hard link count: %u\n", externalFileRecord->hardLinkCount);
				fprintf(stdout, "  First attribute offset: %u\n", externalFileRecord->firstAttributeOffset);
				fprintf(stdout, "  Flags: %u\n", externalFileRecord->inUse);
				fprintf(stdout, "  Used size: %u\n", externalFileRecord->usedSize);
				fprintf(stdout, "  Allocated size: %u\n", externalFileRecord->allocatedSize);
				fprintf(stdout, "  File reference to base record: %llu\n", externalFileRecord->fileReference);
				fprintf(stdout, "  Next attribute ID: %u\n", externalFileRecord->nextAttributeID);
                fprintf(stdout, "  Unused: %u\n", externalFileRecord->unused);
				fprintf(stdout, "  Record number: %u\n", externalFileRecord->recordNumber);

				auto externalAttribute = (AttributeHeader*)((uint8_t*)externalFileRecord + externalFileRecord->firstAttributeOffset);
                while (externalAttribute->attributeType != EndMarker) {
                    if (externalAttribute->attributeType == FileName) {
                        if (externalAttribute->nonResident) {
                            const auto fileNameAttribute = (FileNameAttributeHeaderNonResident*)externalAttribute;
                            PrintNonResidentAttribute(fileNameAttribute);
                            fprintf(stdout, "  Parent directory: %llu\n", ExtractParentRecordNumber(fileNameAttribute->parentRecordNumber));
                            fprintf(stdout, "  Sequence Number: %llu\n", ExtractSequenceNumber(fileNameAttribute->sequenceNumber));
                            fprintf(stdout, "  Creation time: %llu\n", fileNameAttribute->creationTime);
                            fprintf(stdout, "  Change time: %llu\n", fileNameAttribute->modificationTime);
                            fprintf(stdout, "  Last write time: %llu\n", fileNameAttribute->metadataModificationTime);
                            fprintf(stdout, "  Last access time: %llu\n", fileNameAttribute->readTime);
                            fprintf(stdout, "  Allocated size: %llu\n", fileNameAttribute->allocatedSize);
                            fprintf(stdout, "  Real size: %llu\n", fileNameAttribute->realSize);
                            fprintf(stdout, "  Flags: %08X\n", fileNameAttribute->filename_flags);
                            fprintf(stdout, "  Reparse: %08X\n", fileNameAttribute->repase);
                            fprintf(stdout, "  File name length: %u\n", fileNameAttribute->fileNameLength);
                            fprintf(stdout, "  File name namespace: %u\n", fileNameAttribute->namespaceType);
                            fprintf(stdout, "  File name: %ls\n", fileNameAttribute->fileName);
                        }
                        else {
                            auto fileNameAttribute = (FileNameAttributeHeaderResident*)externalAttribute;
                            PrintResidentAttribute(fileNameAttribute);
                            fprintf(stdout, "  Parent directory: %llu\n", ExtractParentRecordNumber(fileNameAttribute->parentRecordNumber));
                            fprintf(stdout, "  Sequence Number: %llu\n", ExtractSequenceNumber(fileNameAttribute->sequenceNumber));
                            fprintf(stdout, "  Creation time: %llu\n", fileNameAttribute->creationTime);
                            fprintf(stdout, "  Change time: %llu\n", fileNameAttribute->modificationTime);
                            fprintf(stdout, "  Last write time: %llu\n", fileNameAttribute->metadataModificationTime);
                            fprintf(stdout, "  Last access time: %llu\n", fileNameAttribute->readTime);
                            fprintf(stdout, "  Allocated size: %llu\n", fileNameAttribute->allocatedSize);
                            fprintf(stdout, "  Real size: %llu\n", fileNameAttribute->realSize);
                            fprintf(stdout, "  Flags: %08X\n", fileNameAttribute->filename_flags);
                            fprintf(stdout, "  Reparse: %08X\n", fileNameAttribute->repase);
                            fprintf(stdout, "  File name length: %u\n", fileNameAttribute->fileNameLength);
                            fprintf(stdout, "  File name namespace: %u\n", fileNameAttribute->namespaceType);
                            fprintf(stdout, "  File name: %ls\n", fileNameAttribute->fileName);
                        }
                    } else
                    {
                        fprintf(stdout, "Attribute: %08X\n", attribute->attributeType);
                    }

					externalAttribute = (AttributeHeader*)((uint8_t*)externalAttribute + externalAttribute->length);
                }
            }
            else {
                const auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == FileName) {
            fprintf(stdout, "FileName Attribute:\n");
            if (attribute->nonResident) {
                const auto fileNameAttribute = (FileNameAttributeHeaderNonResident*)attribute;
                PrintNonResidentAttribute(fileNameAttribute);
                fprintf(stdout, "  Parent directory: %llu\n", ExtractParentRecordNumber(fileNameAttribute->parentRecordNumber));
                fprintf(stdout, "  Sequence Number: %llu\n", ExtractSequenceNumber(fileNameAttribute->sequenceNumber));
                fprintf(stdout, "  Creation time: %llu\n", fileNameAttribute->creationTime);
                fprintf(stdout, "  Change time: %llu\n", fileNameAttribute->modificationTime);
                fprintf(stdout, "  Last write time: %llu\n", fileNameAttribute->metadataModificationTime);
                fprintf(stdout, "  Last access time: %llu\n", fileNameAttribute->readTime);
                fprintf(stdout, "  Allocated size: %llu\n", fileNameAttribute->allocatedSize);
                fprintf(stdout, "  Real size: %llu\n", fileNameAttribute->realSize);
                fprintf(stdout, "  Flags: %08X\n", fileNameAttribute->filename_flags);
                fprintf(stdout, "  Reparse: %08X\n", fileNameAttribute->repase);
                fprintf(stdout, "  File name length: %u\n", fileNameAttribute->fileNameLength);
                fprintf(stdout, "  File name namespace: %u\n", fileNameAttribute->namespaceType);
                fprintf(stdout, "  File name: %ls\n", fileNameAttribute->fileName);
            }
            else {
                auto fileNameAttribute = (FileNameAttributeHeaderResident*)attribute;
                PrintResidentAttribute(fileNameAttribute);
                fprintf(stdout, "  Parent directory: %llu\n", ExtractParentRecordNumber(fileNameAttribute->parentRecordNumber));
                fprintf(stdout, "  Sequence Number: %llu\n", ExtractSequenceNumber(fileNameAttribute->sequenceNumber));
                fprintf(stdout, "  Creation time: %llu\n", fileNameAttribute->creationTime);
                fprintf(stdout, "  Change time: %llu\n", fileNameAttribute->modificationTime);
                fprintf(stdout, "  Last write time: %llu\n", fileNameAttribute->metadataModificationTime);
                fprintf(stdout, "  Last access time: %llu\n", fileNameAttribute->readTime);
                fprintf(stdout, "  Allocated size: %llu\n", fileNameAttribute->allocatedSize);
                fprintf(stdout, "  Real size: %llu\n", fileNameAttribute->realSize);
                fprintf(stdout, "  Flags: %08X\n", fileNameAttribute->filename_flags);
                fprintf(stdout, "  Reparse: %08X\n", fileNameAttribute->repase);
                fprintf(stdout, "  File name length: %u\n", fileNameAttribute->fileNameLength);
                fprintf(stdout, "  File name namespace: %u\n", fileNameAttribute->namespaceType);
                fprintf(stdout, "  File name: %ls\n", fileNameAttribute->fileName);
            }
        }
        else if (attribute->attributeType == ObjectId) {
            fprintf(stdout, "ObjectId Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == SecurityDescriptor) {
            fprintf(stdout, "SecurityDescriptor Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == VolumeName) {
            fprintf(stdout, "VolumeName Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == VolumeInformation) {
            fprintf(stdout, "VolumeInformation Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == Data) {
            fprintf(stdout, "Data Attribute:\n");
            if (attribute->nonResident) {
                dataAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(dataAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == IndexRoot) {
            fprintf(stdout, "IndexRoot Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == IndexAllocation) {
            fprintf(stdout, "IndexAllocation Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == Bitmap) {
            fprintf(stdout, "Bitmap Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);

                approximateRecordCount = nonResidentAttribute->attributeSize * 8;
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == ReparsePoint) {
            fprintf(stdout, "ReparsePoint\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == EAInformation) {
            fprintf(stdout, "EAInformation\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == EA) {
            fprintf(stdout, "EA Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == PropertySet) {
            fprintf(stdout, "PropertySet Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == LoggedUtilityStream) {
            fprintf(stdout, "LoggedUtilityStream Attribute:\n");
            if (attribute->nonResident) {
                auto nonResidentAttribute = (NonResidentAttributeHeader*)attribute;
                PrintNonResidentAttribute(nonResidentAttribute);
            }
            else {
                auto residentAttribute = (ResidentAttributeHeader*)attribute;
                PrintResidentAttribute(residentAttribute);
            }
        }
        else if (attribute->attributeType == EndMarker) {
            fprintf(stdout, "EndMarker\n");
            break;
        }
        else {
            fprintf(stdout, "Unknown attribute type %08X\n", attribute->attributeType);
        }

        attribute = (AttributeHeader*)((uint8_t*)attribute + attribute->length);
    }

    assert(dataAttribute);
    RunHeader* dataRun = (RunHeader*)((uint8_t*)dataAttribute + dataAttribute->dataRunsOffset);
    uint64_t clusterNumber = 0, recordsProcessed = 0;

    while (((uint8_t*)dataRun - (uint8_t*)dataAttribute) < dataAttribute->length && dataRun->lengthFieldBytes) {
        uint64_t length = 0, offset = 0;

        for (int i = 0; i < dataRun->lengthFieldBytes; i++) {
            length |= (uint64_t)(((uint8_t*)dataRun)[1 + i]) << (i * 8);
        }

        for (int i = 0; i < dataRun->offsetFieldBytes; i++) {
            offset |= (uint64_t)(((uint8_t*)dataRun)[1 + dataRun->lengthFieldBytes + i]) << (i * 8);
        }

        if (offset & ((uint64_t)1 << (dataRun->offsetFieldBytes * 8 - 1))) {
            for (int i = dataRun->offsetFieldBytes; i < 8; i++) {
                offset |= (uint64_t)0xFF << (i * 8);
            }
        }

        clusterNumber += offset;
        dataRun = (RunHeader*)((uint8_t*)dataRun + 1 + dataRun->lengthFieldBytes + dataRun->offsetFieldBytes);

        uint64_t filesRemaining = length * bytesPerCluster / MFT_FILE_SIZE;
        uint64_t positionInBlock = 0;

        while (filesRemaining) {
            fprintf(stderr, "%d%% ", (int)(recordsProcessed * 100 / approximateRecordCount));

            uint64_t filesToLoad = MFT_FILES_PER_BUFFER;
            if (filesRemaining < MFT_FILES_PER_BUFFER) filesToLoad = filesRemaining;
            Read(&mftBuffer, clusterNumber * bytesPerCluster + positionInBlock, filesToLoad * MFT_FILE_SIZE);
            positionInBlock += filesToLoad * MFT_FILE_SIZE;
            filesRemaining -= filesToLoad;

            for (int i = 0; i < filesToLoad; i++) {
                // Even on an SSD, processing the file records takes only a fraction of the time to read the data,
                // so there's not much point in multithreading this.

                FileRecordHeader* fileRecord = (FileRecordHeader*)(mftBuffer + MFT_FILE_SIZE * i);
                recordsProcessed++;

                if (!fileRecord->inUse) {
                    notInUseCount++;
                    continue;
                }

                AttributeHeader* attribute = (AttributeHeader*)((uint8_t*)fileRecord + fileRecord->firstAttributeOffset);
                assert(fileRecord->magic == 0x454C4946);

                fprintf(stdout, "  Update sequence offset: %u\n", fileRecord->updateSequenceOffset);
                fprintf(stdout, "  Update sequence size: %u\n", fileRecord->updateSequenceSize);
                fprintf(stdout, "  Log file sequence number: %llu\n", fileRecord->logSequence);
                fprintf(stdout, "  Sequence number: %u\n", fileRecord->sequenceNumber);
                fprintf(stdout, "  Hard link count: %u\n", fileRecord->hardLinkCount);
                fprintf(stdout, "  First attribute offset: %u\n", fileRecord->firstAttributeOffset);
                fprintf(stdout, "  Flags: %u\n", fileRecord->inUse);
                fprintf(stdout, "  Used size: %u\n", fileRecord->usedSize);
                fprintf(stdout, "  Allocated size: %u\n", fileRecord->allocatedSize);
                fprintf(stdout, "  File reference to base record: %llu\n", fileRecord->fileReference);
                fprintf(stdout, "  Next attribute ID: %u\n", fileRecord->nextAttributeID);
                fprintf(stdout, "  Unused: %u\n", fileRecord->unused);
                fprintf(stdout, "  Record number: %u\n", fileRecord->recordNumber);

                while ((uint8_t*)attribute - (uint8_t*)fileRecord < MFT_FILE_SIZE) {
                    if (attribute->attributeType == 0x30) {
                        FileNameAttributeHeaderResident* fileNameAttribute = (FileNameAttributeHeaderResident*)attribute;

                        if (fileNameAttribute->namespaceType != 2 && !fileNameAttribute->nonResident) {
                            File file = {};
                            file.parent = fileNameAttribute->parentRecordNumber;
                            file.name = DuplicateName(fileNameAttribute->fileName, fileNameAttribute->fileNameLength);

                            uint64_t oldLength = arrlenu(files);

                            if (fileRecord->recordNumber >= oldLength) {
                                arrsetlen(files, fileRecord->recordNumber + 1);
                                memset(files + oldLength, 0, sizeof(File) * (fileRecord->recordNumber - oldLength));
                            }

                            files[fileRecord->recordNumber] = file;
                        }
                    }
                    else if (attribute->attributeType == 0xFFFFFFFF) {
                        break;
                    }

                    attribute = (AttributeHeader*)((uint8_t*)attribute + attribute->length);
                }
            }
        }
    }

    fprintf(stderr, "\nFound %lld files.\n", arrlen(files));
	fprintf(stderr, "Found %d files not in use.\n", notInUseCount);

    return 0;
}