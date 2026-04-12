#include "pch.h"

#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../internal.h"
#include "ntfs_io.h"

BOOL Read(HANDLE handle, void* buffer, uint64_t from, DWORD count, PDWORD bytesRead) {
    if (ShouldFailRead()) return FALSE;
    LONG high = from >> 32;
    SetFilePointer(handle, from & 0xFFFFFFFF, &high, FILE_BEGIN);
    return ReadFile(handle, buffer, count, bytesRead, nullptr);
}

static bool ApplyFixupInternal(uint8_t* record, uint32_t recordSize) {
    auto* header = (PFILE_RECORD_SEGMENT_HEADER)record;
    uint16_t usaOffset = header->MultiSectorHeader.UpdateSequenceArrayOffset;
    uint16_t usaSize = header->MultiSectorHeader.UpdateSequenceArraySize;

    if (usaSize < 2) return true;
    uint16_t sectorCount = usaSize - 1;

    auto* usa = (uint16_t*)(record + usaOffset);
    uint16_t usn = usa[0];

    for (uint16_t i = 0; i < sectorCount; i++) {
        uint32_t sectorEnd = (i + 1) * 512 - 2;
        if (sectorEnd + 2 > recordSize) break;

        auto* sectorLastWord = (uint16_t*)(record + sectorEnd);
        if (*sectorLastWord != usn) {
            printf("Fixup mismatch at sector %u: expected 0x%04X, got 0x%04X\n", i, usn, *sectorLastWord);
            return false;
        }
        *sectorLastWord = usa[i + 1];
    }
    return true;
}

bool ApplyFixup(uint8_t* record, uint32_t recordSize) {
    return ApplyFixupInternal(record, recordSize);
}

std::vector<DataRun> ParseDataRuns(PATTRIBUTE_RECORD_HEADER attr) {
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

        if (header->offsetFieldBytes > 0 && (offset & ((int64_t)1 << (header->offsetFieldBytes * 8 - 1)))) {
            for (int i = header->offsetFieldBytes; i < 8; i++)
                offset |= (int64_t)0xFF << (i * 8);
        }

        prevCluster += offset;
        runs.push_back({ prevCluster, length });
    }

    return runs;
}

uint8_t* ReadNonResidentData(HANDLE volumeHandle, PATTRIBUTE_RECORD_HEADER attr, uint32_t bytesPerCluster, uint64_t* outSize) {
    auto runs = ParseDataRuns(attr);
    auto fileSize = (uint64_t)attr->Form.Nonresident.FileSize;
    *outSize = fileSize;

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

bool ReadMFTRecord(HANDLE volumeHandle, std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster, uint64_t recordNumber, uint8_t* buffer) {
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

PATTRIBUTE_RECORD_HEADER FindAttribute(uint8_t* record, ATTRIBUTE_TYPE_CODE type) {
    auto* fileRecord = (PFILE_RECORD_SEGMENT_HEADER)record;
    auto* attr = (PATTRIBUTE_RECORD_HEADER)(record + fileRecord->FirstAttributeOffset);

    while (attr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
        if (attr->RecordLength == 0) break;
        if (attr->TypeCode == type) return attr;
        attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attr + attr->RecordLength);
    }

    return nullptr;
}
