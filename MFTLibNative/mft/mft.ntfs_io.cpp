// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.ntfs_io.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <cstddef>
#include <cstring>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../internal.h"
#include "mft.internal.h"

namespace mftlib::ntfs::detail {

#ifdef _WIN32
BOOL Read(HANDLE handle, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead) {
    if (ShouldFailRead()) {
        return FALSE;
    }
    auto high = static_cast<LONG>(from.value >> 32);
    SetFilePointer(handle, static_cast<LONG>(from.value & 0xFFFFFFFF), &high, FILE_BEGIN);
    return ReadFile(handle, buffer, count, bytesRead, nullptr);
}
#endif  // _WIN32

bool ApplyFixup(uint8_t* record, uint32_t recordSize) {
    constexpr uint32_t kSectorSize = 512;
    constexpr uint32_t kWordSize = sizeof(uint16_t);
    const auto* header = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(record);
    const uint32_t usaOffset = header->MultiSectorHeader.UpdateSequenceArrayOffset;
    const uint32_t usaSize = header->MultiSectorHeader.UpdateSequenceArraySize;
    const uint32_t sectorCount = recordSize / kSectorSize;

    // One entry for the update sequence number plus one per sector; the array lies after the
    // fixed header and ends before the first sector's last word, which it replaces.
    if (sectorCount == 0 || usaSize != sectorCount + 1 ||
        usaOffset < offsetof(FILE_RECORD_SEGMENT_HEADER, UpdateSequenceArray) ||
        usaOffset + (usaSize * kWordSize) > kSectorSize - kWordSize) {
        return false;
    }

    const uint8_t* usa = record + usaOffset;
    for (uint32_t sector = 1; sector <= sectorCount; sector++) {
        if (memcmp(record + (sector * kSectorSize) - kWordSize, usa, kWordSize) != 0) {
            return false;
        }
    }
    for (uint32_t sector = 1; sector <= sectorCount; sector++) {
        memcpy(record + (sector * kSectorSize) - kWordSize, usa + (sector * kWordSize), kWordSize);
    }
    return true;
}

std::vector<DataRun> ParseDataRuns(const ATTRIBUTE_RECORD_HEADER* attr) {
    std::vector<DataRun> runs;
    if (attr->FormCode != 1) {
        return runs;
    }

    const auto* runPtr = reinterpret_cast<const uint8_t*>(attr) + attr->Form.Nonresident.MappingPairsOffset;
    const auto* endPtr = reinterpret_cast<const uint8_t*>(attr) + attr->RecordLength;
    int64_t prevCluster = 0;

    while (runPtr < endPtr) {
        const auto* header = reinterpret_cast<const RunHeader*>(runPtr);
        if (header->lengthFieldBytes == 0) {
            break;
        }
        runPtr++;

        uint64_t length = 0;
        for (int i = 0; i < header->lengthFieldBytes && runPtr < endPtr; i++) {
            length |= static_cast<uint64_t>(*runPtr++) << (i * 8);
        }

        uint64_t offsetBits = 0;
        for (int i = 0; i < header->offsetFieldBytes && runPtr < endPtr; i++) {
            offsetBits |= static_cast<uint64_t>(*runPtr++) << (i * 8);
        }

        if (header->offsetFieldBytes > 0 && ((offsetBits & (1ULL << ((header->offsetFieldBytes * 8) - 1))) != 0)) {
            for (int i = header->offsetFieldBytes; i < 8; i++) {
                offsetBits |= 0xFFULL << (i * 8);
            }
        }
        auto offset = static_cast<int64_t>(offsetBits);

        prevCluster += offset;
        runs.push_back({prevCluster, length});
    }

    return runs;
}

#ifdef _WIN32
uint8_t* ReadNonResidentData(HANDLE volumeHandle, const ATTRIBUTE_RECORD_HEADER* attr, uint32_t bytesPerCluster,
                             uint64_t* outSize) {
    auto runs = ParseDataRuns(attr);
    auto fileSize = static_cast<uint64_t>(attr->Form.Nonresident.FileSize);
    *outSize = fileSize;

    uint64_t totalClusterBytes = 0;
    for (const auto& run : runs) {
        totalClusterBytes += run.clusterCount * bytesPerCluster;
    }
    uint64_t allocSize = (std::max)(totalClusterBytes, fileSize);

    auto* buffer = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(malloc(static_cast<size_t>(allocSize)));
    if (buffer == nullptr) {
        return nullptr;
    }

    uint64_t bufferOffset = 0;
    for (const auto& run : runs) {
        uint64_t runBytes = run.clusterCount * bytesPerCluster;
        uint64_t runOffset = 0;
        while (runOffset < runBytes && bufferOffset < allocSize) {
            auto chunkSize = static_cast<DWORD>((std::min)(static_cast<uint64_t>(0x10000000ULL), runBytes - runOffset));
            DWORD bytesRead;
            if (Read(volumeHandle, buffer + bufferOffset,
                     VolumeOffset{(static_cast<uint64_t>(run.clusterOffset) * bytesPerCluster) + runOffset}, chunkSize,
                     &bytesRead) == 0) {
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

bool ReadMFTRecord(HANDLE volumeHandle, const std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster,
                   ParseGeometry geometry, uint8_t* buffer, uint64_t recordNumber) {
    uint64_t byteOffset = recordNumber * geometry.recordSize;
    uint64_t currentOffset = 0;

    for (const auto& run : mftRuns) {
        uint64_t runBytes = run.clusterCount * bytesPerCluster;

        if (byteOffset >= currentOffset && byteOffset < currentOffset + runBytes) {
            uint64_t offsetInRun = byteOffset - currentOffset;
            uint64_t diskOffset = (static_cast<uint64_t>(run.clusterOffset) * bytesPerCluster) + offsetInRun;

            DWORD bytesRead;
            if ((Read(volumeHandle, buffer, VolumeOffset{diskOffset}, geometry.recordSize, &bytesRead) == 0) ||
                bytesRead != geometry.recordSize) {
                return false;
            }
            return ApplyFixup(buffer, geometry.recordSize);
        }

        currentOffset += runBytes;
    }

    return false;
}
#endif  // _WIN32

PATTRIBUTE_RECORD_HEADER FindAttribute(uint8_t* record, ATTRIBUTE_TYPE_CODE type) {
    const auto* fileRecord = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(record);
    auto* attr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + fileRecord->FirstAttributeOffset);

    while (attr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
        if (attr->RecordLength == 0) {
            break;
        }
        if (attr->TypeCode == type) {
            return attr;
        }
        attr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(attr) + attr->RecordLength);
    }

    return nullptr;
}

}  // namespace mftlib::ntfs::detail
