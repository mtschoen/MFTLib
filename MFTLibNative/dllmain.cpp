#include "pch.h"

#include <cassert>
#include <chrono>
#include <thread>
#include <vector>

#include "framework.h"
#include "ntfs.h"
#include "mft_api.h"

using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

static double ElapsedMs(TimePoint start, TimePoint end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

#define EXPORT __declspec(dllexport)

NTFS_BPB bootSector;

uint8_t mftFile[FILE_RECORD_SIZE];
uint8_t extensionRecord[FILE_RECORD_SIZE];

#define MFT_FILES_PER_BUFFER (65536)
uint8_t mftBuffer[MFT_FILES_PER_BUFFER * FILE_RECORD_SIZE];

struct DataRun {
    int64_t clusterOffset;
    uint64_t clusterCount;
};

static BOOL Read(HANDLE handle, void* buffer, uint64_t from, DWORD count, PDWORD bytesRead) {
    LONG high = from >> 32;
    SetFilePointer(handle, from & 0xFFFFFFFF, &high, FILE_BEGIN);
    return ReadFile(handle, buffer, count, bytesRead, nullptr);
}

// Apply Update Sequence Array fixup to a file record.
// NTFS replaces the last 2 bytes of each 512-byte sector with a check value (USN).
// The original bytes are stored in the USA. This reverses that substitution.
static bool ApplyFixup(uint8_t* record, uint32_t recordSize) {
    auto* header = (PFILE_RECORD_SEGMENT_HEADER)record;
    uint16_t usaOffset = header->MultiSectorHeader.UpdateSequenceArrayOffset;
    uint16_t usaSize = header->MultiSectorHeader.UpdateSequenceArraySize; // count includes USN + one entry per sector

    if (usaSize < 2) return true; // nothing to fix up
    uint16_t sectorCount = usaSize - 1;

    auto* usa = (uint16_t*)(record + usaOffset);
    uint16_t usn = usa[0]; // the check value

    for (uint16_t i = 0; i < sectorCount; i++) {
        uint32_t sectorEnd = (i + 1) * 512 - 2;
        if (sectorEnd + 2 > recordSize) break;

        auto* sectorLastWord = (uint16_t*)(record + sectorEnd);
        if (*sectorLastWord != usn) {
            printf("Fixup mismatch at sector %u: expected 0x%04X, got 0x%04X\n", i, usn, *sectorLastWord);
            return false;
        }
        *sectorLastWord = usa[i + 1]; // restore original bytes
    }
    return true;
}

static std::vector<DataRun> ParseDataRuns(PATTRIBUTE_RECORD_HEADER attr) {
    std::vector<DataRun> runs;
    if (attr->FormCode != 1) return runs;

    auto* runPtr = (uint8_t*)attr + attr->Form.Nonresident.MappingPairsOffset;
    auto* endPtr = (uint8_t*)attr + attr->RecordLength;
    int64_t prevCluster = 0;

    while (runPtr < endPtr) {
        auto* header = (RunHeader*)runPtr;
        if (header->lengthFieldBytes == 0) break;
        runPtr++;

        uint64_t length = 0;
        for (int i = 0; i < header->lengthFieldBytes && runPtr < endPtr; i++)
            length |= (uint64_t)(*runPtr++) << (i * 8);

        int64_t offset = 0;
        for (int i = 0; i < header->offsetFieldBytes && runPtr < endPtr; i++)
            offset |= (uint64_t)(*runPtr++) << (i * 8);

        // Sign-extend if negative
        if (header->offsetFieldBytes > 0 && (offset & ((int64_t)1 << (header->offsetFieldBytes * 8 - 1)))) {
            for (int i = header->offsetFieldBytes; i < 8; i++)
                offset |= (int64_t)0xFF << (i * 8);
        }

        prevCluster += offset;
        runs.push_back({ prevCluster, length });
    }

    return runs;
}

// Read non-resident attribute data. Caller must free() the returned buffer.
static uint8_t* ReadNonResidentData(HANDLE volumeHandle, PATTRIBUTE_RECORD_HEADER attr, uint32_t bytesPerCluster, uint64_t* outSize) {
    auto runs = ParseDataRuns(attr);
    auto fileSize = (uint64_t)attr->Form.Nonresident.FileSize;
    *outSize = fileSize;

    // Allocate for full clusters (reads must be sector-aligned)
    uint64_t totalClusterBytes = 0;
    for (auto& run : runs) totalClusterBytes += run.clusterCount * bytesPerCluster;
    uint64_t allocSize = max(totalClusterBytes, fileSize);

    auto* buffer = (uint8_t*)malloc((size_t)allocSize);
    if (!buffer) return nullptr;

    uint64_t bufferOffset = 0;
    for (auto& run : runs) {
        uint64_t runBytes = run.clusterCount * bytesPerCluster;
        uint64_t runOffset = 0;
        while (runOffset < runBytes && bufferOffset < allocSize) {
            DWORD chunkSize = (DWORD)min((uint64_t)0x10000000, runBytes - runOffset);
            DWORD bytesRead;
            if (!Read(volumeHandle, buffer + bufferOffset, (uint64_t)run.clusterOffset * bytesPerCluster + runOffset, chunkSize, &bytesRead)) {
                free(buffer);
                *outSize = 0;
                return nullptr;
            }
            bufferOffset += bytesRead;
            runOffset += bytesRead;
        }
    }

    return buffer;
}

// Read a specific MFT record by number, using the MFT's data runs to locate it on disk.
static bool ReadMFTRecord(HANDLE volumeHandle, std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster, uint64_t recordNumber, uint8_t* buffer) {
    uint64_t byteOffset = recordNumber * FILE_RECORD_SIZE;
    uint64_t currentOffset = 0;

    for (auto& run : mftRuns) {
        uint64_t runBytes = run.clusterCount * bytesPerCluster;

        if (byteOffset >= currentOffset && byteOffset < currentOffset + runBytes) {
            uint64_t diskOffset = (uint64_t)run.clusterOffset * bytesPerCluster + (byteOffset - currentOffset);
            DWORD bytesRead;
            if (!Read(volumeHandle, buffer, diskOffset, FILE_RECORD_SIZE, &bytesRead) || bytesRead != FILE_RECORD_SIZE)
                return false;
            return ApplyFixup(buffer, FILE_RECORD_SIZE);
        }

        currentOffset += runBytes;
    }

    printf("Error: MFT record %llu not found in data runs (covered %llu bytes, needed offset %llu)\n", recordNumber, currentOffset, byteOffset);
    return false;
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

static const char* AttributeTypeName(ATTRIBUTE_TYPE_CODE type) {
    switch (type) {
        case StandardInformation: return "StandardInformation";
        case AttributeList: return "AttributeList";
        case FileName: return "FileName";
        case ObjectId: return "ObjectId";
        case SecurityDescriptor: return "SecurityDescriptor";
        case VolumeName: return "VolumeName";
        case VolumeInformation: return "VolumeInformation";
        case Data: return "Data";
        case IndexRoot: return "IndexRoot";
        case IndexAllocation: return "IndexAllocation";
        case Bitmap: return "Bitmap";
        case ReparsePoint: return "ReparsePoint";
        case EAInformation: return "EAInformation";
        case EA: return "EA";
        case PropertySet: return "PropertySet";
        case LoggedUtilityStream: return "LoggedUtilityStream";
        case EndMarker: return "EndMarker";
        default: return "Unknown";
    }
}

static void PrintAttributeHeader(PATTRIBUTE_RECORD_HEADER attribute) {
    printf("ATTRIBUTE %p ======================\n", attribute);
    fprintf(stdout, "Attribute Type: %s (0x%X)\n", AttributeTypeName(attribute->TypeCode), attribute->TypeCode);
    fprintf(stdout, "Attribute Record Length: %lu\n", attribute->RecordLength);
    fprintf(stdout, "Attribute Form Code: %u (%s)\n", attribute->FormCode, attribute->FormCode ? "non-resident" : "resident");
    fprintf(stdout, "Attribute Name Length: %u\n", attribute->NameLength);
    fprintf(stdout, "Attribute Name Offset: %u\n", attribute->NameOffset);
    fprintf(stdout, "Attribute Flags: %u\n", attribute->Flags);
    fprintf(stdout, "Attribute Instance: %u\n", attribute->Instance);

    if (attribute->FormCode == 1) {
        fprintf(stdout, "  Lowest VCN: %lld\n", attribute->Form.Nonresident.LowestVcn.QuadPart);
        fprintf(stdout, "  Highest VCN: %lld\n", attribute->Form.Nonresident.HighestVcn.QuadPart);
        fprintf(stdout, "  Allocated Length: %lld\n", attribute->Form.Nonresident.AllocatedLength);
        fprintf(stdout, "  File Size: %lld\n", attribute->Form.Nonresident.FileSize);
        fprintf(stdout, "  Valid Data Length: %lld\n", attribute->Form.Nonresident.ValidDataLength);
    }
}

static BOOL ParseAttributes(PATTRIBUTE_RECORD_HEADER firstAttribute) {
    auto attribute = firstAttribute;
    while (attribute->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker)
    {
        auto attributeLength = attribute->RecordLength;
        if (attributeLength == 0)
            break;

        PrintAttributeHeader(attribute);

        switch (attribute->TypeCode)
        {
        case StandardInformation:
            PrintStandardInformationAttribute((PSTANDARD_INFORMATION)attribute);
            PrintStandardInformationAlt((StandardInformationAttribute*)attribute);
            break;
        case AttributeList:
        case FileName:
        case ObjectId:
        case SecurityDescriptor:
        case VolumeName:
        case VolumeInformation:
        case Data:
        case IndexRoot:
        case IndexAllocation:
        case Bitmap:
        case ReparsePoint:
        case EAInformation:
        case EA:
        case PropertySet:
        case LoggedUtilityStream:
        case EndMarker:
            break;
        }
        attribute = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attribute + attributeLength);
    }
    return true;
}

// Find a specific attribute type in a file record. Returns nullptr if not found.
static PATTRIBUTE_RECORD_HEADER FindAttribute(uint8_t* record, ATTRIBUTE_TYPE_CODE type) {
    auto* fileRecord = (PFILE_RECORD_SEGMENT_HEADER)record;
    auto* attr = (PATTRIBUTE_RECORD_HEADER)(record + fileRecord->FirstAttributeOffset);

    while (attr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
        if (attr->RecordLength == 0) break;
        if (attr->TypeCode == type) return attr;
        attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attr + attr->RecordLength);
    }

    return nullptr;
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

        constexpr DWORD bootSectorSize = 512;
        DWORD bytesRead;
        if (!Read(volumeHandle, &bootSector, 0, bootSectorSize, &bytesRead) || bytesRead != bootSectorSize)
        {
            printf("Error in ParseMFT: Failed to read boot sector. Error: %lu\n", GetLastError());
            return false;
        }

        PrintBootSector();

        if (bootSector.name[0] != 'N' || bootSector.name[1] != 'T' || bootSector.name[2] != 'F' || bootSector.name[3] != 'S')
        {
            printf("Error in ParseMFT: Volume is not NTFS.");
            return false;
        }

        auto bytesPerCluster = bootSector.bytesPerSector * bootSector.sectorsPerCluster;

        // Read MFT file record 0 ($MFT itself)
        bytesRead = 0;
        if (!Read(volumeHandle, &mftFile, bootSector.mftStart * bytesPerCluster, FILE_RECORD_SIZE, &bytesRead) || bytesRead != FILE_RECORD_SIZE)
        {
            printf("Error in ParseMFT: Failed to read MFT file. Error: %lu\n", GetLastError());
            return false;
        }

        ApplyFixup(mftFile, FILE_RECORD_SIZE);

        PATTRIBUTE_RECORD_HEADER firstAttribute;
        ParseMFTFile(&firstAttribute);
        ParseAttributes(firstAttribute);

        // Collect key attributes from record 0
        auto* dataAttr = FindAttribute(mftFile, Data);
        auto* attrListAttr = FindAttribute(mftFile, AttributeList);
        auto* bitmapAttr = FindAttribute(mftFile, Bitmap);

        if (!dataAttr) {
            printf("Error: No Data attribute in $MFT record 0\n");
            return false;
        }

        // Parse the Data attribute's data runs - this tells us where the MFT lives on disk
        auto mftRuns = ParseDataRuns(dataAttr);
        printf("\nMFT DATA RUNS ======================\n");
        printf("  %zu run(s) in base record's Data attribute:\n", mftRuns.size());
        uint64_t totalMftBytes = 0;
        for (size_t i = 0; i < mftRuns.size(); i++) {
            uint64_t runBytes = mftRuns[i].clusterCount * bytesPerCluster;
            printf("    Run %zu: cluster %lld, %llu clusters (%llu bytes)\n",
                i, mftRuns[i].clusterOffset, mftRuns[i].clusterCount, runBytes);
            totalMftBytes += runBytes;
        }
        printf("  Total MFT size from base runs: %llu bytes (%llu records)\n", totalMftBytes, totalMftBytes / FILE_RECORD_SIZE);

        if (bitmapAttr) {
            printf("\nBitmap found directly in record 0\n");
        }

        // If there's an AttributeList, we need to read it to find extension records
        // that contain the Bitmap and possibly additional Data runs
        if (attrListAttr) {
            printf("\nATTRIBUTE LIST ======================\n");

            uint8_t* attrListData = nullptr;
            uint64_t attrListSize = 0;

            if (attrListAttr->FormCode == 1) {
                // Non-resident: read from disk using its data runs
                attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
                printf("  AttributeList is non-resident, %llu bytes\n", attrListSize);
            } else {
                // Resident: data is inline
                attrListSize = attrListAttr->Form.Resident.ValueLength;
                attrListData = (uint8_t*)malloc((size_t)attrListSize);
                if (attrListData)
                    memcpy(attrListData, (uint8_t*)attrListAttr + attrListAttr->Form.Resident.ValueOffset, (size_t)attrListSize);
                printf("  AttributeList is resident, %llu bytes\n", attrListSize);
            }

            if (!attrListData) {
                printf("Error: Failed to read AttributeList data\n");
                return false;
            }

            // Enumerate entries and track which extension records we need to visit
            printf("\n  Entries:\n");
            std::vector<uint64_t> extensionRecords;
            uint64_t offset = 0;

            while (offset + sizeof(ATTRIBUTE_LIST_ENTRY) <= attrListSize) {
                auto* entry = (PATTRIBUTE_LIST_ENTRY)(attrListData + offset);
                if (entry->RecordLength == 0) break;

                uint64_t segNum = (uint64_t)entry->SegmentReference.SegmentNumberLowPart |
                    ((uint64_t)entry->SegmentReference.SegmentNumberHighPart << 32);

                printf("    Type: %-22s (0x%02X)  Record: %5llu  VCN: %lld\n",
                    AttributeTypeName(entry->AttributeTypeCode),
                    entry->AttributeTypeCode, segNum,
                    entry->LowestVcn.QuadPart);

                // Track unique extension record numbers (skip record 0, that's the base)
                if (segNum != 0) {
                    bool found = false;
                    for (auto r : extensionRecords)
                        if (r == segNum) { found = true; break; }
                    if (!found)
                        extensionRecords.push_back(segNum);
                }

                offset += entry->RecordLength;
            }

            printf("\n  Extension records to read: %zu\n", extensionRecords.size());

            // Read each extension record and parse its attributes
            for (auto recNum : extensionRecords) {
                printf("\nEXTENSION RECORD %llu ======================\n", recNum);

                if (!ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, recNum, extensionRecord)) {
                    printf("  Failed to read extension record %llu\n", recNum);
                    continue;
                }

                auto* extFileRecord = (PFILE_RECORD_SEGMENT_HEADER)extensionRecord;
                if (extFileRecord->MultiSectorHeader.Magic != 0x454C4946) {
                    printf("  Invalid magic in extension record %llu\n", recNum);
                    continue;
                }

                auto* extAttr = (PATTRIBUTE_RECORD_HEADER)(extensionRecord + extFileRecord->FirstAttributeOffset);
                while (extAttr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
                    if (extAttr->RecordLength == 0) break;

                    PrintAttributeHeader(extAttr);

                    if (extAttr->TypeCode == Data) {
                        // Additional Data runs for the MFT
                        auto additionalRuns = ParseDataRuns(extAttr);
                        printf("  -> %zu additional MFT data run(s):\n", additionalRuns.size());
                        for (size_t i = 0; i < additionalRuns.size(); i++) {
                            uint64_t runBytes = additionalRuns[i].clusterCount * bytesPerCluster;
                            printf("     Run %zu: cluster %lld, %llu clusters (%llu bytes)\n",
                                i, additionalRuns[i].clusterOffset, additionalRuns[i].clusterCount, runBytes);
                            totalMftBytes += runBytes;
                            mftRuns.push_back(additionalRuns[i]);
                        }
                    }

                    if (extAttr->TypeCode == Bitmap) {
                        printf("  -> Found Bitmap in extension record %llu!\n", recNum);
                        bitmapAttr = extAttr; // Note: points into extensionRecord buffer
                    }

                    extAttr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)extAttr + extAttr->RecordLength);
                }
            }

            free(attrListData);

            printf("\nSUMMARY ======================\n");
            printf("  Total MFT data runs: %zu\n", mftRuns.size());
            printf("  Total MFT size: %llu bytes (%llu records)\n", totalMftBytes, totalMftBytes / FILE_RECORD_SIZE);
            printf("  Bitmap found: %s\n", bitmapAttr ? "yes" : "NO");
        }

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

    EXPORT void FreeMftResult(MftParseResult* result) {
        if (result) {
            free(result->entries);
            free(result->pathEntries);
            free(result);
        }
    }

    // Apply USA protection to a record (reverse of ApplyFixup).
    // Saves original sector-end bytes into USA, replaces them with USN.
    static void ApplyUSAProtection(uint8_t* record, uint32_t recordSize, uint16_t usn) {
        auto* header = (PFILE_RECORD_SEGMENT_HEADER)record;
        uint16_t usaOffset = header->MultiSectorHeader.UpdateSequenceArrayOffset;
        uint16_t usaSize = header->MultiSectorHeader.UpdateSequenceArraySize;
        auto* usa = (uint16_t*)(record + usaOffset);
        usa[0] = usn;

        uint16_t sectorCount = usaSize - 1;
        for (uint16_t i = 0; i < sectorCount; i++) {
            uint32_t sectorEnd = (i + 1) * 512 - 2;
            if (sectorEnd + 2 > recordSize) break;
            auto* sectorLastWord = (uint16_t*)(record + sectorEnd);
            usa[i + 1] = *sectorLastWord; // save original
            *sectorLastWord = usn;         // replace with check value
        }
    }

    // Build a synthetic FILE record with a FileName attribute.
    // The record is USA-protected so that ApplyFixup must be called before parsing.
    // rng is a pointer to the PRNG state so metadata varies per record.
    static void BuildSyntheticRecord(uint8_t* record, uint64_t recordIndex,
                                     uint64_t parentRecord, uint16_t flags,
                                     const wchar_t* name, uint8_t nameLen,
                                     uint64_t baseRef, uint64_t* rng) {
        memset(record, 0, FILE_RECORD_SIZE);

        auto nextRng = [rng]() -> uint64_t {
            *rng ^= *rng << 13;
            *rng ^= *rng >> 7;
            *rng ^= *rng << 17;
            return *rng;
        };

        auto* hdr = (PFILE_RECORD_SEGMENT_HEADER)record;
        hdr->MultiSectorHeader.Magic = 0x454C4946; // "FILE"
        hdr->MultiSectorHeader.UpdateSequenceArrayOffset = 0x30;
        hdr->MultiSectorHeader.UpdateSequenceArraySize = 3; // 1 USN + 2 sector entries
        hdr->SequenceNumber = (uint16_t)(recordIndex + 1);
        hdr->Flags = flags;
        hdr->FirstAttributeOffset = 0x38;

        // Set base file reference for extension records
        hdr->BaseFileRecordSegment.SegmentNumberLowPart = (ULONG)(baseRef & 0xFFFFFFFF);
        hdr->BaseFileRecordSegment.SegmentNumberHighPart = (USHORT)(baseRef >> 32);

        if (baseRef != 0 || !(flags & 0x0001)) {
            auto* endAttr = (PATTRIBUTE_RECORD_HEADER)(record + hdr->FirstAttributeOffset);
            endAttr->TypeCode = EndMarker;
            ApplyUSAProtection(record, FILE_RECORD_SIZE, (uint16_t)(recordIndex & 0xFFFF));
            return;
        }

        // Generate timestamps: spread across 2015-2025 range
        // Windows FILETIME epoch is Jan 1 1601; 2015 ≈ 130645440000000000
        constexpr uint64_t ftBase2015 = 130645440000000000ULL;
        constexpr uint64_t tenYearsTicks = 10ULL * 365 * 24 * 3600 * 10000000ULL;
        uint64_t createTime = ftBase2015 + (nextRng() % tenYearsTicks);
        uint64_t modTime = createTime + (nextRng() % (tenYearsTicks / 10)); // modified after creation
        uint64_t mftModTime = modTime + (nextRng() % 100000000ULL);        // shortly after mod
        uint64_t readTime = modTime + (nextRng() % (tenYearsTicks / 5));    // read sometime later

        bool isDir = (flags & 0x0002) != 0;
        uint64_t fileSize = isDir ? 0 : (nextRng() % (256ULL * 1024 * 1024)); // 0-256MB
        uint64_t allocSize = (fileSize + 4095) & ~4095ULL;                     // round up to 4K cluster
        uint32_t fileAttrs = isDir ? 0x10 : 0x20; // FILE_ATTRIBUTE_DIRECTORY or ARCHIVE
        if (nextRng() % 20 == 0) fileAttrs |= 0x02; // ~5% hidden
        if (nextRng() % 50 == 0) fileAttrs |= 0x04; // ~2% system
        if (nextRng() % 10 == 0) fileAttrs |= 0x01; // ~10% read-only

        uint16_t offset = hdr->FirstAttributeOffset;

        // StandardInformation attribute (type 0x10)
        auto* siAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
        siAttr->TypeCode = StandardInformation;
        siAttr->FormCode = 0; // resident
        siAttr->Form.Resident.ValueLength = 0x48;
        siAttr->Form.Resident.ValueOffset = 0x18;
        siAttr->RecordLength = (0x18 + 0x48 + 7) & ~7;

        // Fill in StandardInformation value: timestamps + permissions
        auto* siValue = (uint8_t*)(record + offset + 0x18);
        memcpy(siValue + 0x00, &createTime, 8);
        memcpy(siValue + 0x08, &modTime, 8);
        memcpy(siValue + 0x10, &mftModTime, 8);
        memcpy(siValue + 0x18, &readTime, 8);
        memcpy(siValue + 0x20, &fileAttrs, 4);  // permissions/flags

        offset += (uint16_t)siAttr->RecordLength;

        // FileName attribute (type 0x30)
        uint16_t fnValueSize = (uint16_t)(sizeof(FILE_NAME) - sizeof(WCHAR) + nameLen * sizeof(WCHAR));
        auto* fnAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
        fnAttr->TypeCode = FileName;
        fnAttr->FormCode = 0; // resident
        fnAttr->Form.Resident.ValueLength = fnValueSize;
        fnAttr->Form.Resident.ValueOffset = 0x18;
        fnAttr->RecordLength = (0x18 + fnValueSize + 7) & ~7;

        auto* fn = (PFILE_NAME)(record + offset + 0x18);
        fn->ParentDirectory.SegmentNumberLowPart = (ULONG)(parentRecord & 0xFFFFFFFF);
        fn->ParentDirectory.SegmentNumberHighPart = (USHORT)(parentRecord >> 32);
        fn->CreationTime = createTime;
        fn->ModificationTime = modTime;
        fn->MftModificationTime = mftModTime;
        fn->ReadTime = readTime;
        fn->AllocatedSize = allocSize;
        fn->FileSize = fileSize;
        fn->FileAttributes = fileAttrs;
        fn->FileNameLength = nameLen;
        fn->Flags = 3; // WIN32_AND_DOS namespace
        wmemcpy(fn->FileName, name, nameLen);

        offset += (uint16_t)fnAttr->RecordLength;

        // Data attribute (type 0x80) — non-resident stub with file size info
        if (!isDir && fileSize > 0) {
            auto* dataAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
            dataAttr->TypeCode = Data;
            dataAttr->FormCode = 1; // non-resident
            dataAttr->NameLength = 0;
            dataAttr->Form.Nonresident.LowestVcn.QuadPart = 0;
            dataAttr->Form.Nonresident.HighestVcn.QuadPart = (allocSize / 4096) - 1;
            dataAttr->Form.Nonresident.MappingPairsOffset = 0x48;
            dataAttr->Form.Nonresident.AllocatedLength = allocSize;
            dataAttr->Form.Nonresident.FileSize = fileSize;
            dataAttr->Form.Nonresident.ValidDataLength = fileSize;
            // Write a single data run: length = allocSize/4096 clusters, offset = recordIndex*64
            // Run header: 1 byte length field, 3 byte offset field -> header byte = 0x31
            uint64_t clusterCount = allocSize / 4096;
            uint64_t clusterOffset = recordIndex * 64; // spread out so offsets don't collide
            auto* runPtr = (uint8_t*)dataAttr + 0x48;
            *runPtr++ = 0x31; // 3 bytes offset, 1 byte length
            *runPtr++ = (uint8_t)(clusterCount & 0xFF);
            *runPtr++ = (uint8_t)(clusterOffset & 0xFF);
            *runPtr++ = (uint8_t)((clusterOffset >> 8) & 0xFF);
            *runPtr++ = (uint8_t)((clusterOffset >> 16) & 0xFF);
            *runPtr++ = 0; // terminator
            dataAttr->RecordLength = (0x48 + 6 + 7) & ~7; // 0x50
            offset += (uint16_t)dataAttr->RecordLength;
        }

        // End marker
        auto* endAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
        endAttr->TypeCode = EndMarker;

        // Apply USA protection so the benchmark exercises the fixup path
        ApplyUSAProtection(record, FILE_RECORD_SIZE, (uint16_t)(recordIndex & 0xFFFF));
    }

    EXPORT bool GenerateSyntheticMFT(const wchar_t* filePath, uint64_t recordCount) {
        HANDLE hFile = CreateFileW(filePath, GENERIC_WRITE, 0, nullptr,
                                   CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) return false;

        // Deterministic PRNG for reproducibility
        uint64_t rng = 12345678901ULL;
        auto nextRng = [&rng]() -> uint32_t {
            rng ^= rng << 13;
            rng ^= rng >> 7;
            rng ^= rng << 17;
            return (uint32_t)(rng & 0xFFFFFFFF);
        };

        // Pre-generate some realistic filenames
        const wchar_t* fileNames[] = {
            L"README.md", L"index.html", L"main.cpp", L"package.json",
            L"Makefile", L"config.yaml", L"data.bin", L"icon.png",
            L"setup.py", L"app.js", L"style.css", L"test.go",
            L"build.gradle", L"Cargo.toml", L"Program.cs", L"pom.xml",
        };
        const wchar_t* dirNames[] = {
            L"src", L"bin", L"obj", L"node_modules", L".git",
            L"build", L"docs", L"tests", L"lib", L"include",
            L"assets", L"scripts", L"config", L"data", L"temp", L"cache",
        };
        constexpr int numFileNames = 16;
        constexpr int numDirNames = 16;

        // Write in bulk using the same mftBuffer (64K records = 64MB chunks)
        uint64_t remaining = recordCount;
        uint64_t recordIndex = 0;

        while (remaining > 0) {
            uint64_t batchSize = min(remaining, (uint64_t)MFT_FILES_PER_BUFFER);

            for (uint64_t i = 0; i < batchSize; i++, recordIndex++) {
                uint8_t* record = mftBuffer + i * FILE_RECORD_SIZE;
                uint32_t r = nextRng();

                if (recordIndex < 5) {
                    BuildSyntheticRecord(record, recordIndex, 0, 0x0001, L"$MFT", 4, 0, &rng);
                } else if (recordIndex == 5) {
                    BuildSyntheticRecord(record, recordIndex, 5, 0x0003, L".", 1, 0, &rng);
                } else if (r % 100 < 10) {
                    // 10% unused/invalid records
                    memset(record, 0, FILE_RECORD_SIZE);
                } else if (r % 100 < 25) {
                    // 15% extension records (skipped by parser)
                    uint64_t baseRec = (nextRng() % recordIndex) + 1;
                    BuildSyntheticRecord(record, recordIndex, 0, 0x0001, L"ext", 3, baseRec, &rng);
                } else if (r % 100 < 40) {
                    // 15% directories
                    uint64_t parent = (recordIndex < 100) ? 5 : (nextRng() % (recordIndex / 2)) + 5;
                    const wchar_t* name = dirNames[nextRng() % numDirNames];
                    BuildSyntheticRecord(record, recordIndex, parent, 0x0003, name, (uint8_t)wcslen(name), 0, &rng);
                } else {
                    // 60% files
                    uint64_t parent = (recordIndex < 100) ? 5 : (nextRng() % (recordIndex / 2)) + 5;
                    const wchar_t* name = fileNames[nextRng() % numFileNames];
                    BuildSyntheticRecord(record, recordIndex, parent, 0x0001, name, (uint8_t)wcslen(name), 0, &rng);
                }
            }

            DWORD written;
            WriteFile(hFile, mftBuffer, (DWORD)(batchSize * FILE_RECORD_SIZE), &written, nullptr);
            remaining -= batchSize;
        }

        CloseHandle(hFile);
        return true;
    }

    // Check if a filename matches a filter (case-insensitive).
    // matchFlags: 1 = exact match, 2 = contains
    static bool FileNameMatches(const wchar_t* name, uint8_t nameLen,
                                const wchar_t* filter, uint16_t filterLen,
                                uint32_t matchFlags) {
        if (matchFlags & 1) {
            // Exact match (case-insensitive)
            if (nameLen != filterLen) return false;
            return _wcsnicmp(name, filter, nameLen) == 0;
        }
        if (matchFlags & 2) {
            // Contains (case-insensitive)
            if (filterLen > nameLen) return false;
            for (uint16_t i = 0; i <= nameLen - filterLen; i++) {
                if (_wcsnicmp(name + i, filter, filterLen) == 0)
                    return true;
            }
            return false;
        }
        return false;
    }

    // Lightweight lookup for path resolution: flat arrays indexed by record number.
    struct PathLookup {
        uint64_t* parents;       // parent record number per record
        uint8_t*  nameLens;      // filename length per record
        uint32_t* nameOffsets;   // offset (in wchar_t units) into namePool
        wchar_t*  namePool;      // concatenated filenames
        uint64_t  namePoolUsed;
        uint64_t  namePoolCapacity;

        bool init(uint64_t totalRecords) {
            parents = (uint64_t*)calloc((size_t)totalRecords, sizeof(uint64_t));
            nameLens = (uint8_t*)calloc((size_t)totalRecords, sizeof(uint8_t));
            nameOffsets = (uint32_t*)calloc((size_t)totalRecords, sizeof(uint32_t));
            // Average ~12 chars per name, ~75% of records are used.
            // Pre-allocate generously since we can't realloc with atomic allocation.
            namePoolCapacity = totalRecords * 16;
            namePool = (wchar_t*)malloc((size_t)namePoolCapacity * sizeof(wchar_t));
            namePoolUsed = 0;
            return parents && nameLens && nameOffsets && namePool;
        }

        // Thread-safe: parents/nameLens/nameOffsets are indexed by unique recordIndex
        // (each thread handles different indices). namePool uses atomic allocation.
        void storeName(uint64_t recordIndex, uint64_t parent, const wchar_t* name, uint8_t nameLen) {
            parents[recordIndex] = parent;
            nameLens[recordIndex] = nameLen;
            uint64_t offset = (uint64_t)InterlockedExchangeAdd64(
                (volatile LONG64*)&namePoolUsed, (LONG64)nameLen);
            if (offset + nameLen > namePoolCapacity) return; // pool exhausted
            nameOffsets[recordIndex] = (uint32_t)offset;
            wmemcpy(namePool + offset, name, nameLen);
        }

        void cleanup() {
            free(parents);
            free(nameLens);
            free(nameOffsets);
            free(namePool);
        }
    };

    // Resolve full path for a record by walking the parent chain.
    // Writes into pathBuf (max pathBufSize wchar_ts). Returns length written.
    static uint16_t ResolvePath(uint64_t recordIndex, PathLookup& lookup, uint64_t totalRecords,
                                wchar_t* pathBuf, uint16_t pathBufSize) {
        // Collect path components by walking up to root (record 5)
        struct Component { const wchar_t* name; uint8_t len; };
        Component stack[128]; // max depth
        int depth = 0;
        uint64_t current = recordIndex;
        uint64_t visited[128];
        int visitCount = 0;

        while (current != 5 && current < totalRecords && depth < 128) {
            // Cycle detection
            bool cycle = false;
            for (int v = 0; v < visitCount; v++)
                if (visited[v] == current) { cycle = true; break; }
            if (cycle) break;
            visited[visitCount++] = current;

            if (lookup.nameLens[current] == 0) break;
            stack[depth].name = lookup.namePool + lookup.nameOffsets[current];
            stack[depth].len = lookup.nameLens[current];
            depth++;
            current = lookup.parents[current];
        }

        // Build path string: component[depth-1]\component[depth-2]\...\component[0]
        uint16_t pos = 0;
        for (int i = depth - 1; i >= 0; i--) {
            if (pos + stack[i].len + 1 >= pathBufSize) break;
            if (pos > 0) pathBuf[pos++] = L'\\';
            wmemcpy(pathBuf + pos, stack[i].name, stack[i].len);
            pos += stack[i].len;
        }
        pathBuf[pos] = L'\0';
        return pos;
    }

    // Thread-local entry collection for parallel parsing.
    struct SliceResult {
        MftFileEntry* entries;
        uint64_t count;
        uint64_t capacity;

        void init(uint64_t cap) {
            entries = (MftFileEntry*)malloc((size_t)cap * sizeof(MftFileEntry));
            count = 0;
            capacity = cap;
        }
        void cleanup() { free(entries); }
    };

    // Process a slice of records from buffer[startIdx..endIdx) into thread-local SliceResult.
    // recordBase is the global record index of buffer[0].
    static void ProcessRecordSlice(
            uint8_t* buffer, uint64_t startIdx, uint64_t endIdx, uint64_t recordBase,
            SliceResult* slice,
            const wchar_t* filter, uint16_t filterLen, uint32_t matchFlags,
            PathLookup* lookup, uint64_t totalRecords) {

        for (uint64_t i = startIdx; i < endIdx; i++) {
            uint64_t recordIndex = recordBase + i;
            auto* recPtr = buffer + FILE_RECORD_SIZE * i;
            auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;

            if (rec->MultiSectorHeader.Magic != 0x454C4946) continue;
            if (!(rec->Flags & 0x0001)) continue;

            uint64_t baseRef = (uint64_t)rec->BaseFileRecordSegment.SegmentNumberLowPart |
                               ((uint64_t)rec->BaseFileRecordSegment.SegmentNumberHighPart << 32);
            if (baseRef != 0) continue;

            auto* attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)rec + rec->FirstAttributeOffset);
            while ((uint8_t*)attr - (uint8_t*)rec < FILE_RECORD_SIZE) {
                if (attr->TypeCode == EndMarker || attr->RecordLength == 0) break;

                if (attr->TypeCode == FileName && attr->FormCode == 0) {
                    auto* fn = (PFILE_NAME)((uint8_t*)attr + attr->Form.Resident.ValueOffset);
                    if (fn->Flags != 2) {
                        uint64_t parent = (uint64_t)fn->ParentDirectory.SegmentNumberLowPart |
                                          ((uint64_t)fn->ParentDirectory.SegmentNumberHighPart << 32);

                        if (lookup && recordIndex < totalRecords) {
                            lookup->storeName(recordIndex, parent, fn->FileName, fn->FileNameLength);
                        }

                        if (filter && !FileNameMatches(fn->FileName, fn->FileNameLength, filter, filterLen, matchFlags))
                            break;

                        if (slice->count >= slice->capacity) {
                            slice->capacity *= 2;
                            slice->entries = (MftFileEntry*)realloc(slice->entries,
                                (size_t)slice->capacity * sizeof(MftFileEntry));
                        }

                        auto& entry = slice->entries[slice->count];
                        memset(&entry, 0, sizeof(MftFileEntry));
                        entry.recordNumber = recordIndex;
                        entry.parentRecordNumber = parent;
                        entry.flags = rec->Flags;
                        entry.fileNameLength = fn->FileNameLength;
                        uint16_t copyLen = min((uint16_t)fn->FileNameLength, (uint16_t)259);
                        wmemcpy_s(entry.fileName, 260, fn->FileName, copyLen);

                        slice->count++;
                        break;
                    }
                }
                attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attr + attr->RecordLength);
            }
        }
    }

    // Shared inner parse loop. Processes filesToLoad records from a buffer,
    // optionally filtering by filename. If filter is nullptr, all used records are emitted.
    // If lookup is non-null, stores parent+name for every used record (for path resolution).
    static void ProcessRecordBatch(
            uint8_t* buffer,
            uint64_t filesToLoad, uint64_t& recordIndex,
            MftParseResult* result, uint64_t& usedCount, uint64_t& capacity,
            const wchar_t* filter, uint16_t filterLen, uint32_t matchFlags,
            PathLookup* lookup, uint64_t totalRecords) {

        for (uint64_t i = 0; i < filesToLoad; i++, recordIndex++) {
            auto* recPtr = buffer + FILE_RECORD_SIZE * i;
            auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;

            if (rec->MultiSectorHeader.Magic != 0x454C4946) continue;
            if (!(rec->Flags & 0x0001)) continue;

            uint64_t baseRef = (uint64_t)rec->BaseFileRecordSegment.SegmentNumberLowPart |
                               ((uint64_t)rec->BaseFileRecordSegment.SegmentNumberHighPart << 32);
            if (baseRef != 0) continue;

            auto* attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)rec + rec->FirstAttributeOffset);
            while ((uint8_t*)attr - (uint8_t*)rec < FILE_RECORD_SIZE) {
                if (attr->TypeCode == EndMarker || attr->RecordLength == 0) break;

                if (attr->TypeCode == FileName && attr->FormCode == 0) {
                    auto* fn = (PFILE_NAME)((uint8_t*)attr + attr->Form.Resident.ValueOffset);
                    if (fn->Flags != 2) {
                        uint64_t parent = (uint64_t)fn->ParentDirectory.SegmentNumberLowPart |
                                          ((uint64_t)fn->ParentDirectory.SegmentNumberHighPart << 32);

                        // Always store in lookup if building paths
                        if (lookup && recordIndex < totalRecords) {
                            lookup->storeName(recordIndex, parent, fn->FileName, fn->FileNameLength);
                        }

                        // Apply filter if present
                        if (filter && !FileNameMatches(fn->FileName, fn->FileNameLength, filter, filterLen, matchFlags))
                            break;

                        if (usedCount >= capacity) {
                            capacity *= 2;
                            auto* grown = (MftFileEntry*)realloc(result->entries, (size_t)capacity * sizeof(MftFileEntry));
                            if (!grown) {
                                swprintf_s(result->errorMessage, 256, L"Failed to grow entry array");
                                result->usedRecords = usedCount;
                                return;
                            }
                            result->entries = grown;
                        }

                        auto& entry = result->entries[usedCount];
                        memset(&entry, 0, sizeof(MftFileEntry));
                        entry.recordNumber = recordIndex;
                        entry.parentRecordNumber = parent;
                        entry.flags = rec->Flags;
                        entry.fileNameLength = fn->FileNameLength;
                        uint16_t copyLen = min((uint16_t)fn->FileNameLength, (uint16_t)259);
                        memset(entry.fileName, 0, sizeof(entry.fileName));
                        wmemcpy_s(entry.fileName, 260, fn->FileName, copyLen);

                        usedCount++;
                        break;
                    }
                }
                attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attr + attr->RecordLength);
            }
        }
    }

    // Callback that reads the next chunk of MFT records into the target buffer.
    // Returns the number of records loaded (0 = done). ioMs accumulates I/O time.
    typedef uint64_t (*ReadChunkFn)(void* context, uint8_t* targetBuffer, double& ioMs);

    // Shared implementation: fixup, parse, filter, and optionally resolve paths.
    // Uses double-buffering to overlap I/O with compute, and parallel threads
    // for fixup and parsing.
    static MftParseResult* ParseMFTImpl(
            ReadChunkFn readChunk, void* readContext,
            uint64_t totalRecords,
            const wchar_t* filter, uint32_t matchFlags) {

        auto wallStart = SteadyClock::now();
        double ioMs = 0, fixupMs = 0, parseMs = 0;

        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (!result) return nullptr;

        result->totalRecords = totalRecords;

        uint16_t filterLen = filter ? (uint16_t)wcslen(filter) : 0;
        bool resolvePaths = filter && (matchFlags & 4);

        PathLookup lookup = {};
        if (resolvePaths) {
            if (!lookup.init(totalRecords)) {
                swprintf_s(result->errorMessage, 256, L"Failed to allocate path lookup");
                return result;
            }
        }

        unsigned numThreads = std::thread::hardware_concurrency();
        if (numThreads < 1) numThreads = 1;

        // Double-buffering: two dynamically allocated buffers
        const size_t bufSize = (size_t)MFT_FILES_PER_BUFFER * FILE_RECORD_SIZE;
        uint8_t* buf[2];
        buf[0] = (uint8_t*)VirtualAlloc(nullptr, bufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        buf[1] = (uint8_t*)VirtualAlloc(nullptr, bufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!buf[0] || !buf[1]) {
            if (buf[0]) VirtualFree(buf[0], 0, MEM_RELEASE);
            if (buf[1]) VirtualFree(buf[1], 0, MEM_RELEASE);
            if (resolvePaths) lookup.cleanup();
            swprintf_s(result->errorMessage, 256, L"Failed to allocate I/O buffers");
            return result;
        }

        uint64_t capacity = filter ? 1024 : totalRecords / 4;
        if (capacity < 1024) capacity = 1024;
        result->entries = (MftFileEntry*)malloc((size_t)capacity * sizeof(MftFileEntry));
        if (!result->entries) {
            VirtualFree(buf[0], 0, MEM_RELEASE);
            VirtualFree(buf[1], 0, MEM_RELEASE);
            if (resolvePaths) lookup.cleanup();
            swprintf_s(result->errorMessage, 256, L"Failed to allocate entry array");
            return result;
        }

        uint64_t usedCount = 0;
        uint64_t recordIndex = 0;

        // Read first chunk
        uint64_t currentChunkSize = readChunk(readContext, buf[0], ioMs);
        int curBuf = 0;

        while (currentChunkSize > 0) {
            // Start async read of next chunk into the other buffer
            uint64_t nextChunkSize = 0;
            double nextIoMs = 0;
            std::thread ioThread([&]() {
                nextChunkSize = readChunk(readContext, buf[1 - curBuf], nextIoMs);
            });

            uint8_t* buffer = buf[curBuf];

            if (numThreads > 1) {
                uint64_t perThread = (currentChunkSize + numThreads - 1) / numThreads;

                // Combined parallel fixup + parse (one thread spawn per chunk)
                auto fixupStart = SteadyClock::now();
                {
                    std::vector<SliceResult> slices(numThreads);
                    std::vector<std::thread> workers;
                    std::vector<double> threadFixupMs(numThreads, 0.0);
                    unsigned actualThreads = 0;

                    for (unsigned t = 0; t < numThreads; t++) {
                        uint64_t tStart = t * perThread;
                        uint64_t tEnd = min(tStart + perThread, currentChunkSize);
                        if (tStart >= currentChunkSize) break;
                        actualThreads++;

                        uint64_t initCap = filter ? 64 : (tEnd - tStart) / 4;
                        if (initCap < 64) initCap = 64;
                        slices[t].init(initCap);

                        workers.emplace_back([buffer, tStart, tEnd, t, &slices, &threadFixupMs,
                                              filter, filterLen, matchFlags,
                                              &lookup, resolvePaths, totalRecords, recordIndex]() {
                            // Fixup phase for this slice
                            auto fStart = SteadyClock::now();
                            for (uint64_t i = tStart; i < tEnd; i++) {
                                auto* recPtr = buffer + FILE_RECORD_SIZE * i;
                                auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;
                                if (rec->MultiSectorHeader.Magic == 0x454C4946)
                                    ApplyFixup(recPtr, FILE_RECORD_SIZE);
                            }
                            threadFixupMs[t] = ElapsedMs(fStart, SteadyClock::now());

                            // Parse phase for this slice
                            ProcessRecordSlice(buffer, tStart, tEnd, recordIndex,
                                               &slices[t], filter, filterLen, matchFlags,
                                               resolvePaths ? &lookup : nullptr, totalRecords);
                        });
                    }
                    for (auto& w : workers) w.join();

                    auto combinedEnd = SteadyClock::now();
                    double totalElapsed = ElapsedMs(fixupStart, combinedEnd);

                    // Report max fixup time across threads (wall clock of parallel fixup)
                    double maxFixup = 0;
                    for (unsigned t = 0; t < actualThreads; t++)
                        if (threadFixupMs[t] > maxFixup) maxFixup = threadFixupMs[t];
                    fixupMs += maxFixup;
                    parseMs += totalElapsed - maxFixup;

                    // Merge thread-local results
                    for (unsigned t = 0; t < actualThreads; t++) {
                        auto& s = slices[t];
                        if (s.count > 0) {
                            if (usedCount + s.count > capacity) {
                                while (usedCount + s.count > capacity) capacity *= 2;
                                result->entries = (MftFileEntry*)realloc(result->entries,
                                    (size_t)capacity * sizeof(MftFileEntry));
                            }
                            memcpy(result->entries + usedCount, s.entries,
                                   (size_t)s.count * sizeof(MftFileEntry));
                            usedCount += s.count;
                        }
                        s.cleanup();
                    }
                }
            } else {
                // Single-threaded fallback
                auto fixupStart = SteadyClock::now();
                for (uint64_t i = 0; i < currentChunkSize; i++) {
                    auto* recPtr = buffer + FILE_RECORD_SIZE * i;
                    auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;
                    if (rec->MultiSectorHeader.Magic == 0x454C4946)
                        ApplyFixup(recPtr, FILE_RECORD_SIZE);
                }
                fixupMs += ElapsedMs(fixupStart, SteadyClock::now());

                auto parseStart = SteadyClock::now();
                ProcessRecordBatch(buffer, currentChunkSize, recordIndex,
                                   result, usedCount, capacity,
                                   filter, filterLen, matchFlags,
                                   resolvePaths ? &lookup : nullptr, totalRecords);
                parseMs += ElapsedMs(parseStart, SteadyClock::now());
            }

            recordIndex += currentChunkSize;

            // Wait for I/O to complete
            ioThread.join();
            ioMs += nextIoMs;

            currentChunkSize = nextChunkSize;
            curBuf = 1 - curBuf;
        }

        VirtualFree(buf[0], 0, MEM_RELEASE);
        VirtualFree(buf[1], 0, MEM_RELEASE);

        if (resolvePaths && usedCount > 0) {
            result->pathEntries = (MftPathEntry*)calloc((size_t)usedCount, sizeof(MftPathEntry));
            if (result->pathEntries) {
                for (uint64_t i = 0; i < usedCount; i++) {
                    auto& src = result->entries[i];
                    auto& dst = result->pathEntries[i];
                    dst.recordNumber = src.recordNumber;
                    dst.parentRecordNumber = src.parentRecordNumber;
                    dst.flags = src.flags;
                    dst.pathLength = ResolvePath(src.recordNumber, lookup, totalRecords,
                                                dst.path, 1024);
                }
            }
            lookup.cleanup();
        }

        result->usedRecords = usedCount;
        result->ioTimeMs = ioMs;
        result->fixupTimeMs = fixupMs;
        result->parseTimeMs = parseMs;
        result->totalTimeMs = ElapsedMs(wallStart, SteadyClock::now());
        return result;
    }

    // --- Volume reader: reads MFT chunks via data runs from a live NTFS volume ---

    struct VolumeReadContext {
        HANDLE volumeHandle;
        std::vector<DataRun>* mftRuns;
        uint32_t bytesPerCluster;
        size_t runIndex;
        uint64_t filesRemaining;  // in current run
        uint64_t positionInBlock; // byte offset within current run
    };

    static uint64_t VolumeReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
        auto* c = (VolumeReadContext*)ctx;
        while (c->filesRemaining == 0) {
            c->runIndex++;
            if (c->runIndex >= c->mftRuns->size()) return 0;
            auto& run = (*c->mftRuns)[c->runIndex];
            c->filesRemaining = run.clusterCount * c->bytesPerCluster / FILE_RECORD_SIZE;
            c->positionInBlock = 0;
        }

        auto& run = (*c->mftRuns)[c->runIndex];
        uint64_t filesToLoad = min(c->filesRemaining, (uint64_t)MFT_FILES_PER_BUFFER);
        DWORD readBytes;
        auto ioStart = SteadyClock::now();
        if (!Read(c->volumeHandle, targetBuffer,
                  (uint64_t)run.clusterOffset * c->bytesPerCluster + c->positionInBlock,
                  (DWORD)(filesToLoad * FILE_RECORD_SIZE), &readBytes)) {
            return 0;
        }
        ioMs += ElapsedMs(ioStart, SteadyClock::now());
        c->positionInBlock += filesToLoad * FILE_RECORD_SIZE;
        c->filesRemaining -= filesToLoad;
        return filesToLoad;
    }

    // Parse all MFT records from a volume, optionally filtering by filename.
    // If filter is nullptr, all used records are returned.
    // matchFlags: bit 0 (1) = exact match, bit 1 (2) = contains, bit 2 (4) = resolve paths.
    EXPORT MftParseResult* ParseMFTRecords(HANDLE volumeHandle, const wchar_t* filter, uint32_t matchFlags) {
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (!result) return nullptr;

        if (volumeHandle == INVALID_HANDLE_VALUE) {
            swprintf_s(result->errorMessage, 256, L"Volume handle is invalid");
            return result;
        }

        // Read boot sector
        NTFS_BPB bpb;
        DWORD bytesRead;
        if (!Read(volumeHandle, &bpb, 0, 512, &bytesRead) || bytesRead != 512) {
            swprintf_s(result->errorMessage, 256, L"Failed to read boot sector. Error: %lu", GetLastError());
            return result;
        }
        if (bpb.name[0] != 'N' || bpb.name[1] != 'T' || bpb.name[2] != 'F' || bpb.name[3] != 'S') {
            swprintf_s(result->errorMessage, 256, L"Volume is not NTFS");
            return result;
        }

        uint32_t bytesPerCluster = bpb.bytesPerSector * bpb.sectorsPerCluster;

        // Read MFT record 0 ($MFT itself)
        uint8_t record0[FILE_RECORD_SIZE];
        if (!Read(volumeHandle, record0, bpb.mftStart * bytesPerCluster, FILE_RECORD_SIZE, &bytesRead)
            || bytesRead != FILE_RECORD_SIZE) {
            swprintf_s(result->errorMessage, 256, L"Failed to read MFT record 0");
            return result;
        }

        ApplyFixup(record0, FILE_RECORD_SIZE);

        auto* fileRecord0 = (PFILE_RECORD_SEGMENT_HEADER)record0;
        if (fileRecord0->MultiSectorHeader.Magic != 0x454C4946) {
            swprintf_s(result->errorMessage, 256, L"Invalid MFT record 0 magic");
            return result;
        }

        // Find Data attribute -> MFT data runs
        auto* dataAttr = FindAttribute(record0, Data);
        if (!dataAttr) {
            swprintf_s(result->errorMessage, 256, L"No Data attribute in MFT record 0");
            return result;
        }
        auto mftRuns = ParseDataRuns(dataAttr);

        // Handle AttributeList -> extension records with additional Data runs
        auto* attrListAttr = FindAttribute(record0, AttributeList);
        if (attrListAttr) {
            uint8_t* attrListData = nullptr;
            uint64_t attrListSize = 0;

            if (attrListAttr->FormCode == 1) {
                attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
            } else {
                attrListSize = attrListAttr->Form.Resident.ValueLength;
                attrListData = (uint8_t*)malloc((size_t)attrListSize);
                if (attrListData)
                    memcpy(attrListData, (uint8_t*)attrListAttr + attrListAttr->Form.Resident.ValueOffset, (size_t)attrListSize);
            }

            if (attrListData) {
                std::vector<uint64_t> extensionRecords;
                uint64_t offset = 0;
                while (offset + sizeof(ATTRIBUTE_LIST_ENTRY) <= attrListSize) {
                    auto* entry = (PATTRIBUTE_LIST_ENTRY)(attrListData + offset);
                    if (entry->RecordLength == 0) break;
                    uint64_t segNum = (uint64_t)entry->SegmentReference.SegmentNumberLowPart |
                        ((uint64_t)entry->SegmentReference.SegmentNumberHighPart << 32);
                    if (segNum != 0) {
                        bool found = false;
                        for (auto r : extensionRecords) if (r == segNum) { found = true; break; }
                        if (!found) extensionRecords.push_back(segNum);
                    }
                    offset += entry->RecordLength;
                }

                uint8_t extRecord[FILE_RECORD_SIZE];
                for (auto recNum : extensionRecords) {
                    if (!ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, recNum, extRecord))
                        continue;
                    auto* extHdr = (PFILE_RECORD_SEGMENT_HEADER)extRecord;
                    if (extHdr->MultiSectorHeader.Magic != 0x454C4946) continue;

                    auto* extAttr = (PATTRIBUTE_RECORD_HEADER)(extRecord + extHdr->FirstAttributeOffset);
                    while (extAttr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
                        if (extAttr->RecordLength == 0) break;
                        if (extAttr->TypeCode == Data) {
                            auto additionalRuns = ParseDataRuns(extAttr);
                            for (auto& r : additionalRuns) mftRuns.push_back(r);
                        }
                        extAttr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)extAttr + extAttr->RecordLength);
                    }
                }
                free(attrListData);
            }
        }

        // Calculate total MFT records
        uint64_t totalMftBytes = 0;
        for (auto& run : mftRuns) totalMftBytes += run.clusterCount * bytesPerCluster;
        uint64_t totalRecords = totalMftBytes / FILE_RECORD_SIZE;

        // Set up first run for the reader
        VolumeReadContext ctx = {};
        ctx.volumeHandle = volumeHandle;
        ctx.mftRuns = &mftRuns;
        ctx.bytesPerCluster = bytesPerCluster;
        ctx.runIndex = 0;
        ctx.filesRemaining = mftRuns[0].clusterCount * bytesPerCluster / FILE_RECORD_SIZE;
        ctx.positionInBlock = 0;

        free(result); // ParseMFTImpl allocates its own
        return ParseMFTImpl(VolumeReadChunk, &ctx, totalRecords, filter, matchFlags);
    }

    // --- File reader: reads MFT chunks sequentially from a flat file ---

    struct FileReadContext {
        HANDLE hFile;
        uint64_t recordsRemaining;
    };

    static uint64_t FileReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
        auto* c = (FileReadContext*)ctx;
        if (c->recordsRemaining == 0) return 0;
        uint64_t filesToLoad = min(c->recordsRemaining, (uint64_t)MFT_FILES_PER_BUFFER);
        DWORD readBytes;
        auto ioStart = SteadyClock::now();
        if (!ReadFile(c->hFile, targetBuffer, (DWORD)(filesToLoad * FILE_RECORD_SIZE), &readBytes, nullptr)) {
            return 0;
        }
        ioMs += ElapsedMs(ioStart, SteadyClock::now());
        c->recordsRemaining -= filesToLoad;
        return filesToLoad;
    }

    // Parse MFT records from a flat file (e.g. synthetic MFT for benchmarking).
    // Supports the same filter/matchFlags as ParseMFTRecords.
    EXPORT MftParseResult* ParseMFTFromFile(const wchar_t* filePath, const wchar_t* filter, uint32_t matchFlags) {
        HANDLE hFile = CreateFileW(filePath, GENERIC_READ, FILE_SHARE_READ, nullptr,
                                   OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) {
            auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
            if (result) swprintf_s(result->errorMessage, 256, L"Failed to open file. Error: %lu", GetLastError());
            return result;
        }

        LARGE_INTEGER fileSize;
        GetFileSizeEx(hFile, &fileSize);
        uint64_t totalRecords = fileSize.QuadPart / FILE_RECORD_SIZE;

        FileReadContext ctx = { hFile, totalRecords };
        auto* result = ParseMFTImpl(FileReadChunk, &ctx, totalRecords, filter, matchFlags);
        CloseHandle(hFile);
        return result;
    }
}
