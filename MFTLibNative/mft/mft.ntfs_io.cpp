// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.ntfs_io.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <cstddef>
#include <cstring>
#include <limits>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../internal.h"
#include "mft.internal.h"

#ifndef _WIN32
    #include <unistd.h>
#endif

namespace mftlib::ntfs::detail {

#ifdef _WIN32
BOOL Read(HANDLE handle, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead) {
    *bytesRead = 0;
    if (ShouldFailRead()) {
        return FALSE;
    }
    // The offset travels with the request, so reads on one handle cannot move each other's position.
    OVERLAPPED position{};
    position.Offset = static_cast<DWORD>(from.value & 0xFFFFFFFF);
    position.OffsetHigh = static_cast<DWORD>(from.value >> 32);
    if (ReadFile(handle, buffer, count, bytesRead, &position) != 0) {
        return TRUE;
    }
    // Like a read on a plain file, one that starts at the end succeeds with no bytes.
    return GetLastError() == ERROR_HANDLE_EOF ? TRUE : FALSE;
}
#else
// The handle is a file descriptor. Like ReadFile on a file, a read that reaches the end of the
// file succeeds with fewer bytes than asked for.
BOOL Read(HANDLE handle, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead) {
    *bytesRead = 0;
    if (ShouldFailRead()) {
        return FALSE;
    }
    const auto fileDescriptor = static_cast<int>(reinterpret_cast<intptr_t>(handle));
    auto* target = static_cast<uint8_t*>(buffer);
    while (*bytesRead < count) {
        const ssize_t got = RetryInterruptedSystemCall([&]() {
            return ::pread(fileDescriptor, target + *bytesRead, count - *bytesRead,
                           static_cast<off_t>(from.value + *bytesRead));
        });
        if (got < 0) {
            return FALSE;
        }
        if (got == 0) {
            break;
        }
        *bytesRead += static_cast<DWORD>(got);
    }
    return TRUE;
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

namespace {
constexpr uint64_t kMaximumVolumeOffset = static_cast<uint64_t>((std::numeric_limits<int64_t>::max)());
constexpr size_t kMinimumFirstAttributeOffset = 42;
constexpr size_t kResidentAttributeHeaderSize = 0x18;
constexpr size_t kNonresidentAttributeHeaderSize = 0x40;
constexpr size_t kMaximumRunFieldBytes = sizeof(uint64_t);

bool TryMultiply(uint64_t factor, uint64_t multiplier, uint64_t* product) {
    if (factor != 0 && multiplier > (std::numeric_limits<uint64_t>::max)() / factor) {
        return false;
    }
    *product = factor * multiplier;
    return true;
}

bool TryAdd(uint64_t addend, uint64_t other, uint64_t* sum) {
    if (other > (std::numeric_limits<uint64_t>::max)() - addend) {
        return false;
    }
    *sum = addend + other;
    return true;
}

bool IsWellFormedNonresident(const ATTRIBUTE_RECORD_HEADER* attr) {
    return attr->FormCode == 1 && attr->RecordLength >= kNonresidentAttributeHeaderSize &&
           attr->Form.Nonresident.MappingPairsOffset >= kNonresidentAttributeHeaderSize &&
           attr->Form.Nonresident.MappingPairsOffset <= attr->RecordLength;
}

// Reads a little-endian run field of fieldBytes (at most eight) bytes; a signed field is sign-extended.
uint64_t ReadRunField(const uint8_t* field, size_t fieldBytes, bool isSigned) {
    uint64_t value = 0;
    for (size_t i = 0; i < fieldBytes; i++) {
        value |= static_cast<uint64_t>(field[i]) << (i * 8);
    }
    if (isSigned && fieldBytes > 0 && (field[fieldBytes - 1] & 0x80) != 0) {
        for (size_t i = fieldBytes; i < kMaximumRunFieldBytes; i++) {
            value |= 0xFFULL << (i * 8);
        }
    }
    return value;
}
}  // namespace

bool TryClusterByteOffset(uint64_t cluster, uint32_t bytesPerCluster, uint64_t* byteOffset) {
    return TryMultiply(cluster, bytesPerCluster, byteOffset) && *byteOffset <= kMaximumVolumeOffset;
}

bool TryMeasureRuns(const std::vector<DataRun>& runs, uint32_t bytesPerCluster, uint64_t* totalBytes) {
    uint64_t total = 0;
    for (const auto& run : runs) {
        uint64_t runBytes = 0;
        uint64_t start = 0;
        uint64_t end = 0;
        if (run.clusterOffset < 0 || !TryMultiply(run.clusterCount, bytesPerCluster, &runBytes) ||
            !TryClusterByteOffset(static_cast<uint64_t>(run.clusterOffset), bytesPerCluster, &start) ||
            !TryAdd(start, runBytes, &end) || end > kMaximumVolumeOffset || !TryAdd(total, runBytes, &total)) {
            return false;
        }
    }
    *totalBytes = total;
    return true;
}

std::vector<DataRun> ParseDataRuns(const ATTRIBUTE_RECORD_HEADER* attr) {
    std::vector<DataRun> runs;
    if (!IsWellFormedNonresident(attr)) {
        return runs;
    }

    const auto* runPtr = reinterpret_cast<const uint8_t*>(attr) + attr->Form.Nonresident.MappingPairsOffset;
    const auto* endPtr = reinterpret_cast<const uint8_t*>(attr) + attr->RecordLength;
    uint64_t cluster = 0;

    while (runPtr < endPtr) {
        const auto* header = reinterpret_cast<const RunHeader*>(runPtr);
        if (header->lengthFieldBytes == 0) {
            break;
        }
        const size_t lengthBytes = header->lengthFieldBytes;
        const size_t offsetBytes = header->offsetFieldBytes;
        if (lengthBytes > kMaximumRunFieldBytes || offsetBytes > kMaximumRunFieldBytes ||
            static_cast<size_t>(endPtr - runPtr) < 1 + lengthBytes + offsetBytes) {
            return {};
        }
        runPtr++;

        const uint64_t length = ReadRunField(runPtr, lengthBytes, false);
        runPtr += lengthBytes;
        const uint64_t offset = ReadRunField(runPtr, offsetBytes, true);
        runPtr += offsetBytes;

        cluster += offset;
        runs.push_back({static_cast<int64_t>(cluster), length});
    }

    return runs;
}

uint8_t* ReadNonResidentData(HANDLE volumeHandle, const ATTRIBUTE_RECORD_HEADER* attr, uint32_t bytesPerCluster,
                             uint64_t* outSize) {
    *outSize = 0;
    if (!IsWellFormedNonresident(attr)) {
        return nullptr;
    }
    const auto runs = ParseDataRuns(attr);
    const auto fileSize = static_cast<uint64_t>(attr->Form.Nonresident.FileSize);
    uint64_t totalClusterBytes = 0;
    if (!TryMeasureRuns(runs, bytesPerCluster, &totalClusterBytes) || fileSize == 0 || fileSize > totalClusterBytes) {
        return nullptr;
    }
    // Reads are whole clusters. The runs hold fileSize, so rounding it up to a cluster cannot overflow.
    const uint64_t remainder = fileSize % bytesPerCluster;
    const uint64_t readLimit = remainder == 0 ? fileSize : fileSize + (bytesPerCluster - remainder);

    auto* buffer = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(malloc(static_cast<size_t>(readLimit)));
    if (buffer == nullptr) {
        return nullptr;
    }

    uint64_t bufferOffset = 0;
    for (const auto& run : runs) {
        const uint64_t runBytes = run.clusterCount * bytesPerCluster;
        const uint64_t runStart = static_cast<uint64_t>(run.clusterOffset) * bytesPerCluster;
        uint64_t runOffset = 0;
        while (runOffset < runBytes && bufferOffset < readLimit) {
            auto chunkSize = static_cast<DWORD>(
                (std::min)({static_cast<uint64_t>(0x10000000ULL), runBytes - runOffset, readLimit - bufferOffset}));
            DWORD bytesRead = 0;
            if (Read(volumeHandle, buffer + bufferOffset, VolumeOffset{runStart + runOffset}, chunkSize, &bytesRead) ==
                    0 ||
                bytesRead != chunkSize) {
                free(buffer);
                return nullptr;
            }
            bufferOffset += bytesRead;
            runOffset += bytesRead;
        }
    }

    *outSize = fileSize;
    return buffer;
}

const wchar_t* ReadMFTRecord(HANDLE volumeHandle, const std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster,
                             ParseGeometry geometry, uint8_t* buffer, uint64_t recordNumber) {
    // The runs are validated by the caller (TryMeasureRuns on the base runs, and on each extension's
    // runs before they were appended), so their lengths and disk offsets below cannot overflow. A segment number
    // has 48 bits and a record size at most 16, so the byte offset fits 64 bits.
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
                return kIncompleteVolumeReadMessage;
            }
            const auto* header = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(buffer);
            if (header->MultiSectorHeader.Magic != kFileRecordMagic) {
                return L"Invalid MFT extension record magic";
            }
            return ApplyFixup(buffer, geometry.recordSize) ? nullptr : L"MFT extension record has an invalid fixup";
        }

        currentOffset += runBytes;
    }

    return kIncompleteVolumeReadMessage;
}

AttributeStep NextAttribute(uint8_t* record, uint32_t recordSize, size_t* offset, PATTRIBUTE_RECORD_HEADER* attribute) {
    if (*offset < kMinimumFirstAttributeOffset || *offset + sizeof(uint32_t) > recordSize) {
        return AttributeStep::Malformed;
    }
    auto* current = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + *offset);
    if (current->TypeCode == ATTRIBUTE_TYPE_CODE::EndMarker) {
        return AttributeStep::End;
    }
    if (*offset + kResidentAttributeHeaderSize > recordSize || current->RecordLength < kResidentAttributeHeaderSize ||
        *offset + current->RecordLength > recordSize) {
        return AttributeStep::Malformed;
    }
    *attribute = current;
    *offset += current->RecordLength;
    return AttributeStep::Attribute;
}

AttributeSearch FindAttribute(uint8_t* record, uint32_t recordSize, ATTRIBUTE_TYPE_CODE type) {
    size_t offset = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(record)->FirstAttributeOffset;
    PATTRIBUTE_RECORD_HEADER attribute = nullptr;
    AttributeStep step = NextAttribute(record, recordSize, &offset, &attribute);
    while (step == AttributeStep::Attribute) {
        if (attribute->TypeCode == type) {
            return {attribute, false};
        }
        step = NextAttribute(record, recordSize, &offset, &attribute);
    }
    return {nullptr, step == AttributeStep::Malformed};
}

}  // namespace mftlib::ntfs::detail
