#pragma once

#include <atomic>
#include <cstdint>
#include <cstdlib>
// aislop-ignore-next-line CppUnusedIncludeDirective -- memcpy below needs this on GCC/Clang
#include <cstring>
#include <vector>

#include "../framework.h"
#include "../mft_api.h"
#include "../internal.h"

namespace mftlib::ntfs {
struct DataRun {
    int64_t clusterOffset;
    uint64_t clusterCount;
};

// Strong type for a byte offset into the raw volume, so it cannot be transposed
// with the adjacent byte-count argument of Read().
struct VolumeOffset {
    uint64_t value;
};

// Named (not anonymous) internal namespace: these helpers are defined in the
// mft.ntfs_io.cpp fragment and used from the parse fragments, so they need a
// shared declaration here. An anonymous namespace in a header is an ODR hazard
// (each including TU would get its own internal-linkage copy).
namespace detail {
bool ApplyFixup(uint8_t* record, uint32_t recordSize);
std::vector<DataRun> ParseDataRuns(const ATTRIBUTE_RECORD_HEADER* attr);
PATTRIBUTE_RECORD_HEADER FindAttribute(uint8_t* record, ATTRIBUTE_TYPE_CODE type);
#ifdef _WIN32
BOOL Read(HANDLE handle, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead);
uint8_t* ReadNonResidentData(HANDLE volumeHandle, const ATTRIBUTE_RECORD_HEADER* attr, uint32_t bytesPerCluster,
                             uint64_t* outSize);
bool ReadMFTRecord(HANDLE volumeHandle, const std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster,
                   ParseGeometry geometry, uint8_t* buffer, uint64_t recordNumber);
#endif
}  // namespace detail
}  // namespace mftlib::ntfs

// Bundles the filename-filter parameters so they travel as a single argument
// (and cannot be transposed) through the record-parsing path.
struct FilterSpec {
    const wchar_t* text;  // null = no filter (accept every named record)
    uint16_t length;      // wchar_t units in text
    uint32_t flags;       // match bitfield: 1=exact, 2=substring, 4=resolve paths
};

// Half-open record range [start, end) within a chunk buffer.
struct SliceRange {
    uint64_t start;
    uint64_t end;
};

struct PathLookup {
    uint64_t* parents = nullptr;
    uint8_t* nameLens = nullptr;
    uint32_t* nameOffsets = nullptr;
    // namePool stores raw NTFS UTF-16 bytes (2 bytes per WCHAR unit).
    // On Windows wchar_t==WCHAR so this is a direct match.
    // On Linux wchar_t is 32-bit, so we use a byte pool and keep sizes in code units.
    uint8_t* namePool = nullptr;
    std::atomic<uint64_t> namePoolUsed{0};
    uint64_t namePoolCapacity = 0;
    // Count of names dropped because the pool filled up. Nonzero means some
    // resolved paths are truncated; surfaced to the caller via errorMessage.
    std::atomic<uint64_t> namesDropped{0};

    bool init(uint64_t totalRecords) {
        parents = static_cast<uint64_t*>(calloc(totalRecords, sizeof(uint64_t)));
        nameLens = static_cast<uint8_t*>(calloc(totalRecords, sizeof(uint8_t)));
        nameOffsets = static_cast<uint32_t*>(calloc(totalRecords, sizeof(uint32_t)));
        // Each name entry can be up to 255 WCHAR units = 510 bytes; use 32 bytes avg * 2 for bytes.
        // A test hook can shrink the pool to exercise the exhaustion path.
        uint64_t capacityOverride = NamePoolCapacityOverride();
        namePoolCapacity = (capacityOverride != 0U) ? capacityOverride : totalRecords * 64;  // bytes
        namePool = static_cast<uint8_t*>(malloc(namePoolCapacity));
        namePoolUsed = 0;
        namesDropped = 0;
        return (parents != nullptr) && (nameLens != nullptr) && (nameOffsets != nullptr) && (namePool != nullptr);
    }

    // Store a name from an NTFS FileName attribute (WCHAR* = char16_t*, nameLen in WCHAR units)
    void storeName(uint64_t recordIndex, uint64_t parent, const WCHAR* name, uint8_t nameLen) {
        parents[recordIndex] = parent;
        uint64_t byteCount = static_cast<uint64_t>(nameLen) * sizeof(WCHAR);
        uint64_t offset = namePoolUsed.fetch_add(byteCount, std::memory_order_relaxed);
        if (offset + byteCount > namePoolCapacity) {
            namesDropped.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        nameOffsets[recordIndex] = static_cast<uint32_t>(offset);
        memcpy(namePool + offset, name, byteCount);
        nameLens[recordIndex] = nameLen;
    }

    void cleanup() const {
        free(parents);
        free(nameLens);
        free(nameOffsets);
        free(namePool);
    }
};

struct ParsedEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint32_t fileAttributes;
    uint16_t flags;
    const WCHAR* name;
    uint16_t nameLength;
};

// Per-worker accumulator. Uses std::vector so growth is exception-safe;
// the merge step copies entries and strings out.
struct SliceResult {
    std::vector<MftCompactEntry> entries;
    std::vector<uint16_t> strings;

    void append(const ParsedEntry& entry) {
        uint64_t stringOffset = strings.size();
        if (entry.nameLength > 0 && entry.name != nullptr) {
            const auto* src = reinterpret_cast<const uint16_t*>(entry.name);
            strings.insert(strings.end(), src, src + entry.nameLength);
        }
        const MftCompactEntry compact{entry.recordNumber, entry.parentRecordNumber, stringOffset, entry.fileAttributes,
                                      entry.flags,        entry.nameLength};
        entries.push_back(compact);
    }
};

// Growable compact output bookkeeping.
struct CompactOutput {
    MftCompactEntry* entries = nullptr;
    uint64_t entryCount = 0;
    uint64_t entryCapacity = 0;
    uint16_t* strings = nullptr;
    uint64_t stringUnits = 0;
    uint64_t stringCapacity = 0;
};

// Read-only inputs that steer record scanning: the name filter, the optional
// path-lookup table (null when paths aren't resolved), and the total record
// count. Threaded through the scan pipeline as one const& instead of three
// same-purpose arguments that could be transposed at a call site.
struct ScanContext {
    FilterSpec filter{};
    PathLookup* lookup = nullptr;
    uint64_t totalRecords = 0;
    ParseGeometry geometry{};
};

constexpr uint32_t MAX_NTFS_PATH_UNITS = 32767;

bool ResolvePath(uint64_t recordIndex, const PathLookup& lookup, uint64_t totalRecords, std::vector<uint16_t>& path);
bool AppendSlice(CompactOutput& output, const SliceResult& slice, wchar_t* errorMessage);
void ProcessRecordSlice(uint8_t* buffer, SliceRange range, uint64_t recordBase, SliceResult* slice,
                        const ScanContext& scan);
void ProcessRecordBatch(uint8_t* buffer, uint64_t filesToLoad, uint64_t& recordIndex, SliceResult& batchSlice,
                        const ScanContext& scan);

using ReadChunkFn = uint64_t (*)(void* context, uint8_t* targetBuffer, double& ioMs);

MftParseResult* ParseMFTImpl(ReadChunkFn readChunk, void* readContext, uint64_t totalRecords, FilterSpec filter,
                             uint32_t bufferSizeRecords, ParseGeometry geometry);
