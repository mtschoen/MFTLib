// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.ntfs_io.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <algorithm>
#include <cerrno>
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

namespace {
bool Complies(const VolumeAlignment& alignment, const void* buffer, uint64_t from, uint64_t count) {
    return reinterpret_cast<uintptr_t>(buffer) % alignment.memoryBytes == 0 && from % alignment.sectorBytes == 0 &&
           count % alignment.sectorBytes == 0;
}

// Under SetVolumeAlignmentOverride, a read the armed alignment does not allow fails as a noncached handle's would.
bool BreaksArmedAlignment(const void* buffer, uint64_t from, uint64_t count) {
    const VolumeAlignment armed{VolumeSectorAlignmentOverride(), VolumeMemoryAlignmentOverride()};
    return armed.sectorBytes != 0 && !Complies(armed, buffer, from, count);
}

// Leaves the reason a read was not issued where the platform's last error is read.
void SetReadError(const bool outOfMemory) {
#ifdef _WIN32
    SetLastError(outOfMemory ? ERROR_NOT_ENOUGH_MEMORY : ERROR_INVALID_PARAMETER);
#else
    errno = outOfMemory ? ENOMEM : EINVAL;
#endif
}

#ifdef _WIN32
bool IsPowerOfTwo(const uint32_t value) { return value != 0 && (value & (value - 1)) == 0; }

VolumeAlignment QueryVolumeAlignment(HANDLE handle) {
    STORAGE_PROPERTY_QUERY query{};
    query.PropertyId = StorageAccessAlignmentProperty;
    query.QueryType = PropertyStandardQuery;
    STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR descriptor{};
    DWORD returned = 0;
    if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query), &descriptor, sizeof(descriptor),
                        &returned, nullptr) != 0 &&
        returned >= offsetof(STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR, BytesOffsetForSectorAlignment) &&
        IsPowerOfTwo(descriptor.BytesPerLogicalSector) && IsPowerOfTwo(descriptor.BytesPerPhysicalSector)) {
        return {descriptor.BytesPerLogicalSector,
                (std::max)(descriptor.BytesPerLogicalSector, descriptor.BytesPerPhysicalSector)};
    }
    // The volume data is followed by the extended data, which carries the physical sector size.
    struct VolumeDataWithExtension {
        NTFS_VOLUME_DATA_BUFFER data;
        NTFS_EXTENDED_VOLUME_DATA extension;
    } volumeData{};
    returned = 0;
    if (DeviceIoControl(handle, FSCTL_GET_NTFS_VOLUME_DATA, nullptr, 0, &volumeData, sizeof(volumeData), &returned,
                        nullptr) != 0 &&
        returned >= sizeof(NTFS_VOLUME_DATA_BUFFER) && IsPowerOfTwo(volumeData.data.BytesPerSector)) {
        const uint32_t logical = volumeData.data.BytesPerSector;
        const bool extended = returned >= sizeof(volumeData) &&
                              volumeData.extension.ByteCount >= sizeof(NTFS_EXTENDED_VOLUME_DATA) &&
                              IsPowerOfTwo(volumeData.extension.BytesPerPhysicalSector) &&
                              volumeData.extension.BytesPerPhysicalSector >= logical;
        return {logical, extended ? volumeData.extension.BytesPerPhysicalSector : logical};
    }
    return {};
}

// One positioned read with the destination, offset and length exactly as given.
BOOL ReadAt(HANDLE handle, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead) {
    *bytesRead = 0;
    if (ShouldFailRead()) {
        return FALSE;
    }
    if (BreaksArmedAlignment(buffer, from.value, count)) {
        SetReadError(false);
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
BOOL ReadAt(HANDLE handle, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead) {
    *bytesRead = 0;
    if (ShouldFailRead()) {
        return FALSE;
    }
    if (BreaksArmedAlignment(buffer, from.value, count)) {
        SetReadError(false);
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

// Reads the whole sectors around [from, from + count) into an aligned buffer and copies the asked-for bytes out.
BOOL ReadThroughBounceBuffer(const RawVolume& volume, uint8_t* buffer, uint64_t from, DWORD count, PDWORD bytesRead) {
    const uint64_t sector = volume.alignment.sectorBytes;
    const uint64_t start = from - (from % sector);
    const uint64_t head = from - start;
    const uint64_t end = from + count + ((sector - ((from + count) % sector)) % sector);
    const uint64_t span = end - start;
    const uint64_t memory = volume.alignment.memoryBytes;
    mftlib::platform::AlignedBuffer bounce;
    if (span > (std::numeric_limits<DWORD>::max)()) {
        SetReadError(false);
        return FALSE;
    }
    if (!bounce.TryReserve(static_cast<size_t>(span + memory))) {
        SetReadError(true);
        return FALSE;
    }
    NoteBouncedVolumeRead();
    const auto address = reinterpret_cast<uintptr_t>(bounce.data());
    auto* aligned = bounce.data() + ((memory - (address % memory)) % memory);
    DWORD arrived = 0;
    if (ReadAt(volume.handle, aligned, VolumeOffset{start}, static_cast<DWORD>(span), &arrived) == 0) {
        return FALSE;
    }
    const uint64_t delivered = arrived > head ? (std::min)(arrived - head, static_cast<uint64_t>(count)) : 0;
    memcpy(buffer, aligned + head, static_cast<size_t>(delivered));
    *bytesRead = static_cast<DWORD>(delivered);
    return TRUE;
}
}  // namespace

RawVolume OpenRawVolume(HANDLE handle) {
    RawVolume volume{handle, {}};
    if (const uint32_t armed = VolumeSectorAlignmentOverride(); armed != 0) {
        volume.alignment = {armed, VolumeMemoryAlignmentOverride()};
        return volume;
    }
#ifdef _WIN32
    volume.alignment = QueryVolumeAlignment(handle);
#endif
    return volume;
}

BOOL Read(const RawVolume& volume, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead) {
    if (Complies(volume.alignment, buffer, from.value, count)) {
        return ReadAt(volume.handle, buffer, from, count, bytesRead);
    }
    *bytesRead = 0;
    return ReadThroughBounceBuffer(volume, static_cast<uint8_t*>(buffer), from.value, count, bytesRead);
}

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

bool IsWellFormedNonresident(const ATTRIBUTE_RECORD_HEADER* attribute) {
    return attribute->FormCode == 1 && attribute->RecordLength >= kNonresidentAttributeHeaderSize &&
           attribute->Form.Nonresident.MappingPairsOffset >= kNonresidentAttributeHeaderSize &&
           attribute->Form.Nonresident.MappingPairsOffset <= attribute->RecordLength;
}

// Reads a little-endian run field of fieldBytes (at most eight) bytes; a signed field is sign-extended.
uint64_t ReadRunField(const uint8_t* field, size_t fieldBytes, bool isSigned) {
    uint64_t value = 0;
#ifdef _MSC_VER
    #pragma loop(no_vector)
#endif
    for (size_t i = 0; i < fieldBytes; i++) {
        value |= static_cast<uint64_t>(field[i]) << (i * 8);
    }
    if (isSigned && fieldBytes > 0 && (field[fieldBytes - 1] & 0x80) != 0) {
#ifdef _MSC_VER
    #pragma loop(no_vector)
#endif
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

std::vector<DataRun> ParseDataRuns(const ATTRIBUTE_RECORD_HEADER* attribute) {
    std::vector<DataRun> runs;
    if (!IsWellFormedNonresident(attribute)) {
        return runs;
    }

    const auto* runPointer =
        reinterpret_cast<const uint8_t*>(attribute) + attribute->Form.Nonresident.MappingPairsOffset;
    const auto* endPointer = reinterpret_cast<const uint8_t*>(attribute) + attribute->RecordLength;
    uint64_t cluster = 0;

    while (runPointer < endPointer) {
        const auto* header = reinterpret_cast<const RunHeader*>(runPointer);
        if (header->lengthFieldBytes == 0) {
            break;
        }
        const size_t lengthBytes = header->lengthFieldBytes;
        const size_t offsetBytes = header->offsetFieldBytes;
        if (lengthBytes > kMaximumRunFieldBytes || offsetBytes > kMaximumRunFieldBytes ||
            static_cast<size_t>(endPointer - runPointer) < 1 + lengthBytes + offsetBytes) {
            return {};
        }
        runPointer++;

        const uint64_t length = ReadRunField(runPointer, lengthBytes, false);
        runPointer += lengthBytes;
        const uint64_t offset = ReadRunField(runPointer, offsetBytes, true);
        runPointer += offsetBytes;

        cluster += offset;
        runs.push_back({static_cast<int64_t>(cluster), length});
    }

    return runs;
}

bool ReadNonResidentData(const RawVolume& volume, const ATTRIBUTE_RECORD_HEADER* attribute, uint32_t bytesPerCluster,
                         mftlib::platform::AlignedBuffer* data, uint64_t* outSize) {
    *outSize = 0;
    if (!IsWellFormedNonresident(attribute)) {
        return false;
    }
    const auto runs = ParseDataRuns(attribute);
    const auto fileSize = static_cast<uint64_t>(attribute->Form.Nonresident.FileSize);
    uint64_t totalClusterBytes = 0;
    if (!TryMeasureRuns(runs, bytesPerCluster, &totalClusterBytes) || fileSize == 0 || fileSize > totalClusterBytes) {
        return false;
    }
    // Reads are whole clusters. The runs hold fileSize, so rounding it up to a cluster cannot overflow.
    const uint64_t remainder = fileSize % bytesPerCluster;
    const uint64_t readLimit = remainder == 0 ? fileSize : fileSize + (bytesPerCluster - remainder);

    if (ShouldFailAllocation() || !data->TryReserve(static_cast<size_t>(readLimit))) {
        return false;
    }
    uint8_t* buffer = data->data();

    uint64_t bufferOffset = 0;
    for (const auto& run : runs) {
        const uint64_t runBytes = run.clusterCount * bytesPerCluster;
        const uint64_t runStart = static_cast<uint64_t>(run.clusterOffset) * bytesPerCluster;
        uint64_t runOffset = 0;
        while (runOffset < runBytes && bufferOffset < readLimit) {
            auto chunkSize = static_cast<DWORD>(
                (std::min)({static_cast<uint64_t>(0x10000000ULL), runBytes - runOffset, readLimit - bufferOffset}));
            DWORD bytesRead = 0;
            if (Read(volume, buffer + bufferOffset, VolumeOffset{runStart + runOffset}, chunkSize, &bytesRead) == 0 ||
                bytesRead != chunkSize) {
                return false;
            }
            bufferOffset += bytesRead;
            runOffset += bytesRead;
        }
    }

    *outSize = fileSize;
    return true;
}

const wchar_t* ReadMFTRecord(const RawVolume& volume, const std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster,
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
            if ((Read(volume, buffer, VolumeOffset{diskOffset}, geometry.recordSize, &bytesRead) == 0) ||
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
