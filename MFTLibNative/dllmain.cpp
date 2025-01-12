#include "pch.h"

#include <cassert>

#include "framework.h"
#include "ntfs.h"

#define EXPORT __declspec(dllexport)

NTFS_BPB bootSector;

uint8_t mftFile[FILE_RECORD_SIZE];

#define MFT_FILES_PER_BUFFER (65536)
uint8_t mftBuffer[MFT_FILES_PER_BUFFER * FILE_RECORD_SIZE];

static BOOL Read(HANDLE handle, void* buffer, uint64_t from, DWORD count, PDWORD bytesRead) {
    LONG high = from >> 32;
    SetFilePointer(handle, from & 0xFFFFFFFF, &high, FILE_BEGIN);
    return ReadFile(handle, buffer, count, bytesRead, nullptr);
}

static void PrintStandardInformationAttribute(PSTANDARD_INFORMATION attribute)
{
    fprintf(stdout, "Standard Information Attribute:\n");
    fprintf(stdout, "  Reserved: ");

    for (int i = 0; i < 0x30; i++)
    {
        fprintf(stdout, "%02X", attribute->Reserved[i]);
    }

    fprintf(stdout, "\n");

    fprintf(stdout, "  Owner ID: %u\n", attribute->OwnerId);
    fprintf(stdout, "  Security ID: %u\n", attribute->SecurityId);
}

static void PrintStandardInformationAlt(StandardInformationAttribute* attribute)
{
    fprintf(stdout, "Standard Information Attribute alt:\n");
	fprintf(stdout, "  Creation time: %llu\n", attribute->creationTime);
	fprintf(stdout, "  Modification time: %llu\n", attribute->modificationTime);
	fprintf(stdout, "  Metadata modification time: %llu\n", attribute->metadataModificationTime);
	fprintf(stdout, "  Read time: %llu\n", attribute->readTime);
	fprintf(stdout, "  Permissions: %u\n", attribute->permissions);
	fprintf(stdout, "  Max versions: %u\n", attribute->maxVersions);
	fprintf(stdout, "  Version: %u\n", attribute->version);
	fprintf(stdout, "  Class ID: %u\n", attribute->classId);
	fprintf(stdout, "  Owner ID: %u\n", attribute->ownerId);
	fprintf(stdout, "  Security ID: %u\n", attribute->securityId);
	fprintf(stdout, "  Quota: %llu\n", attribute->quota);
	fprintf(stdout, "  Update sequence: %llu\n", attribute->updateSequence);
}

static BOOL ParseAttributes(PATTRIBUTE_RECORD_HEADER firstAttribute) {
    auto attribute = firstAttribute;
    while (attribute->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker)
    {
        auto attributeLength = attribute->RecordLength;
        if (attributeLength == 0)
            break;

        printf("ATTRIBUTE %p======================\n", firstAttribute);

        auto attributeType = attribute->TypeCode;
        fprintf(stdout, "Attribute Type: %u\n", attributeType);
        fprintf(stdout, "Attribute Record Length: %lu\n", attributeLength);
        fprintf(stdout, "Attribute Form Code: %u\n", attribute->FormCode);
        fprintf(stdout, "Attribute Name Length: %u\n", attribute->NameLength);
        fprintf(stdout, "Attribute Name Offset: %u\n", attribute->NameOffset);
        fprintf(stdout, "Attribute Flags: %u\n", attribute->Flags);
        fprintf(stdout, "Attribute Instance: %u\n", attribute->Instance);

        switch (attributeType)
        {
        case StandardInformation:
            PrintStandardInformationAttribute((PSTANDARD_INFORMATION)attribute);
            PrintStandardInformationAlt((StandardInformationAttribute*)attribute);
            break;
        case AttributeList:
            break;
        case FileName:
            break;
        case ObjectId:
            break;
        case SecurityDescriptor:
            break;
        case VolumeName:
            break;
        case VolumeInformation:
            break;
        case Data:
            break;
        case IndexRoot:
            break;
        case IndexAllocation:
            break;
        case Bitmap:
            break;
        case ReparsePoint:
            break;
        case EAInformation:
            break;
        case EA:
            break;
        case PropertySet:
            break;
        case LoggedUtilityStream:
            break;
        case EndMarker:
            break;
        }
        attribute = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attribute + attributeLength);
    }
    return true;
}

static void PrintFileRecord(PFILE_RECORD_SEGMENT_HEADER fileRecord) {
    printf("FILE RECORD %p ======================\n", fileRecord);

    assert(fileRecord->MultiSectorHeader.Magic == 0x454C4946);
    fprintf(stdout, "  Update sequence offset: %u\n", fileRecord->MultiSectorHeader.UpdateSequenceArrayOffset);
    fprintf(stdout, "  Update sequence size: %u\n", fileRecord->MultiSectorHeader.UpdateSequenceArraySize);
    fprintf(stdout, "  Log file sequence number: %llu\n", fileRecord->Reserved1);
    fprintf(stdout, "  Sequence number: %u\n", fileRecord->SequenceNumber);
    fprintf(stdout, "  Hard link count: %u\n", fileRecord->Reserved2);
    fprintf(stdout, "  First attribute offset: %u\n", fileRecord->FirstAttributeOffset);
    fprintf(stdout, "  Flags: %u\n", fileRecord->Flags);

    fprintf(stdout, "  Reserved3[0] (Used size?): %lu\n", fileRecord->Reserved3[0]);
    fprintf(stdout, "  Reserved3[1] (Allocated size?): %lu\n", fileRecord->Reserved3[1]);
    fprintf(stdout, "  File reference to base record:\n");
    fprintf(stdout, "    Sequence Number: %hu\n", fileRecord->BaseFileRecordSegment.SequenceNumber);
    fprintf(stdout, "    Segment Number Low Part: %lu\n", fileRecord->BaseFileRecordSegment.SegmentNumberLowPart);
    fprintf(stdout, "    Segment Number High Part: %hu\n", fileRecord->BaseFileRecordSegment.SegmentNumberHighPart);
    fprintf(stdout, "  Reserved4: %u\n", fileRecord->Reserved4);
    fprintf(stdout, "  Update Sequence Array: %hu\n", fileRecord->UpdateSequenceArray[0]);
}

static void ParseMFTFile(ATTRIBUTE_RECORD_HEADER** firstAttribute) {
    auto fileRecord = (PFILE_RECORD_SEGMENT_HEADER)mftFile;
    *firstAttribute = (PATTRIBUTE_RECORD_HEADER)(mftFile + fileRecord->FirstAttributeOffset);
    assert(fileRecord->MultiSectorHeader.Magic == 0x454C4946);

    PrintFileRecord(fileRecord);
}

static void PrintBootSector() {
    fprintf(stdout, "Boot Sector=================================\n");
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
}

extern "C" {
    EXPORT bool ParseMFT(HANDLE volumeHandle) {
        if (volumeHandle == INVALID_HANDLE_VALUE)
        {
            printf("Error in ParseMFT: Volume handle is invalid.");
            return false;
        }

        // Assume cluster size is 512 bytes; ReadFile on volume handles must be integer multiples of sector size
        // https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering
        constexpr DWORD bootSectorSize = 512;
        DWORD bytesRead;
        if (!Read(volumeHandle, &bootSector, 0, bootSectorSize, &bytesRead) || bytesRead != bootSectorSize)
        {
            printf("Error in ParseMFT:Failed to read boot sector. Error: %lu\n", GetLastError());
            return false;
        }

        PrintBootSector();

        if (bootSector.name[0] != 'N' || bootSector.name[1] != 'T' || bootSector.name[2] != 'F' || bootSector.name[3] != 'S')
        {
            printf("Error in ParseMFT: Volume is not NTFS.");
            return false;
        }

        auto bytesPerCluster = bootSector.bytesPerSector * bootSector.sectorsPerCluster;

        // TODO: Fall back to MFT mirror if MFT is corrupted
        bytesRead = 0;
        if (!Read(volumeHandle, &mftFile, bootSector.mftStart * bytesPerCluster, FILE_RECORD_SIZE, &bytesRead) || bytesRead != FILE_RECORD_SIZE)
        {
            printf("Error in ParseMFT:Failed to read MFT file. Error: %lu\n", GetLastError());
            return false;
        }

        PATTRIBUTE_RECORD_HEADER firstAttribute;
        ParseMFTFile(&firstAttribute);
        ParseAttributes(firstAttribute);

        return true;
    }

    EXPORT void PrintVolumeInfo(HANDLE volumeHandle) {
        if (volumeHandle == INVALID_HANDLE_VALUE)
        {
            printf("Error in PrintVolumeInfo: Volume handle is invalid.");
            return;
        }

        auto combinedVolumeData = NTFS_COMBINED_VOLUME_DATA();
        DWORD bytesReturned;

        try
        {
            DWORD combinedBufferSize = sizeof(NTFS_COMBINED_VOLUME_DATA);
            if (!DeviceIoControl(volumeHandle, FSCTL_GET_NTFS_VOLUME_DATA, nullptr, 0, &combinedVolumeData, combinedBufferSize, &bytesReturned, nullptr))
            {
                printf("Failed to get NTFS volume data. Error: %lu\n", GetLastError());
                return;
            }

            if (bytesReturned != combinedBufferSize)
            {
                printf("Failed to get NTFS volume data. Buffer sizes don't match. Error: %lu\n", GetLastError());
                return;
            }

            printf("VOLUME DATA======================\n");

            auto volumeData = combinedVolumeData.StandardData;
            printf("Volume Serial Number: %lld\n", volumeData.VolumeSerialNumber.QuadPart);
            printf("Number Sectors: %lld\n", volumeData.NumberSectors.QuadPart);
            printf("Total Clusters: %lld\n", volumeData.TotalClusters.QuadPart);
            printf("Free Clusters: %lld\n", volumeData.FreeClusters.QuadPart);
            printf("Total Reserved: %lld\n", volumeData.TotalReserved.QuadPart);
            printf("Bytes Per Sector: %lu\n", volumeData.BytesPerSector);
            printf("Bytes Per Cluster: %lu\n", volumeData.BytesPerCluster);
            printf("Bytes Per File Record Segment: %lu\n", volumeData.BytesPerFileRecordSegment);
            printf("Clusters Per File Record Segment: %lu\n", volumeData.ClustersPerFileRecordSegment);
            printf("MFT Valid Data Length: %lld\n", volumeData.MftValidDataLength.QuadPart);
            printf("MFT Start LCN: %lld\n", volumeData.MftStartLcn.QuadPart);
            printf("MFT2 Start LCN: %lld\n", volumeData.Mft2StartLcn.QuadPart);
            printf("MFT Zone Start: %lld\n", volumeData.MftZoneStart.QuadPart);
            printf("MFT Zone End: %lld\n", volumeData.MftZoneEnd.QuadPart);

            auto extendedVolumeData = combinedVolumeData.ExtendedData;
            printf("Byte Count: %lu\n", extendedVolumeData.ByteCount);
            printf("Major Version: %d\n", extendedVolumeData.MajorVersion);
            printf("Minor Version: %d\n", extendedVolumeData.MinorVersion);
            printf("Bytes Per Physical Sector: %lu\n", extendedVolumeData.BytesPerPhysicalSector);
            printf("Lfs Major Version: %d\n", extendedVolumeData.LfsMajorVersion);
            printf("Lfs Minor Version: %d\n", extendedVolumeData.LfsMinorVersion);
        }
        catch (...)
        {
            printf("Failed to get volume info. Error: %lu\n", GetLastError());
        }
    }
}
