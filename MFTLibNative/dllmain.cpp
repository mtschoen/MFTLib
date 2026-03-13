#include "pch.h"

#include <cassert>

#include "framework.h"
#include "ntfs.h"
#include "mft_api.h"

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

    EXPORT MftParseResult* ParseMFTRecords(HANDLE volumeHandle) {
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
                // Collect unique extension record numbers
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

                // Read extension records for additional Data runs
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
        result->totalRecords = totalRecords;

        // Allocate entry array with growing capacity
        uint64_t capacity = totalRecords / 4;
        if (capacity < 1024) capacity = 1024;
        result->entries = (MftFileEntry*)malloc((size_t)capacity * sizeof(MftFileEntry));
        if (!result->entries) {
            swprintf_s(result->errorMessage, 256, L"Failed to allocate entry array");
            return result;
        }

        uint64_t usedCount = 0;
        uint64_t recordIndex = 0;

        for (auto& run : mftRuns) {
            uint64_t runBytes = run.clusterCount * bytesPerCluster;
            uint64_t filesRemaining = runBytes / FILE_RECORD_SIZE;
            uint64_t positionInBlock = 0;

            while (filesRemaining > 0) {
                uint64_t filesToLoad = min(filesRemaining, (uint64_t)MFT_FILES_PER_BUFFER);
                DWORD readBytes;
                if (!Read(volumeHandle, mftBuffer,
                          (uint64_t)run.clusterOffset * bytesPerCluster + positionInBlock,
                          (DWORD)(filesToLoad * FILE_RECORD_SIZE), &readBytes)) {
                    break;
                }
                positionInBlock += filesToLoad * FILE_RECORD_SIZE;
                filesRemaining -= filesToLoad;

                for (uint64_t i = 0; i < filesToLoad; i++, recordIndex++) {
                    auto* recPtr = mftBuffer + FILE_RECORD_SIZE * i;
                    auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;

                    // Skip invalid or not-in-use records
                    if (rec->MultiSectorHeader.Magic != 0x454C4946) continue;

                    ApplyFixup(recPtr, FILE_RECORD_SIZE);
                    if (!(rec->Flags & 0x0001)) continue;

                    // Skip extension records (base file reference != 0)
                    uint64_t baseRef = (uint64_t)rec->BaseFileRecordSegment.SegmentNumberLowPart |
                                       ((uint64_t)rec->BaseFileRecordSegment.SegmentNumberHighPart << 32);
                    if (baseRef != 0) continue;

                    // Find FileName attribute (skip DOS-only namespace)
                    auto* attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)rec + rec->FirstAttributeOffset);
                    while ((uint8_t*)attr - (uint8_t*)rec < FILE_RECORD_SIZE) {
                        if (attr->TypeCode == EndMarker || attr->RecordLength == 0) break;

                        if (attr->TypeCode == FileName && attr->FormCode == 0) {
                            auto* fn = (PFILE_NAME)((uint8_t*)attr + attr->Form.Resident.ValueOffset);
                            if (fn->Flags != 2) { // Not DOS-only namespace
                                // Grow array if needed
                                if (usedCount >= capacity) {
                                    capacity *= 2;
                                    auto* grown = (MftFileEntry*)realloc(result->entries, (size_t)capacity * sizeof(MftFileEntry));
                                    if (!grown) {
                                        swprintf_s(result->errorMessage, 256, L"Failed to grow entry array");
                                        result->usedRecords = usedCount;
                                        return result;
                                    }
                                    result->entries = grown;
                                }

                                auto& entry = result->entries[usedCount];
                                memset(&entry, 0, sizeof(MftFileEntry));
                                entry.recordNumber = recordIndex;
                                entry.parentRecordNumber = (uint64_t)fn->ParentDirectory.SegmentNumberLowPart |
                                                           ((uint64_t)fn->ParentDirectory.SegmentNumberHighPart << 32);
                                entry.flags = rec->Flags;
                                entry.fileNameLength = fn->FileNameLength;
                                uint16_t copyLen = min((uint16_t)fn->FileNameLength, (uint16_t)259);
                                // Null-terminate the source buffer first to ensure safe copy
                                memset(entry.fileName, 0, sizeof(entry.fileName));
                                wmemcpy_s(entry.fileName, 260, fn->FileName, copyLen);

                                usedCount++;
                                break; // Use first matching FileName
                            }
                        }
                        attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attr + attr->RecordLength);
                    }
                }
            }
        }

        result->usedRecords = usedCount;
        return result;
    }

    EXPORT void FreeMftResult(MftParseResult* result) {
        if (result) {
            free(result->entries);
            free(result);
        }
    }
}
