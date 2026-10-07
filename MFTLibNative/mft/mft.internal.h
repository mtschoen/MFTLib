#pragma once

#include <algorithm>
#include <cstdint>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../mft_api.h"

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
// Checks the update sequence array before reading or writing through it, then restores each
// sector's last word. False when the array does not sit inside the first sector after the fixed
// header, does not cover exactly the record's sectors, or a sector's last word is not the update
// sequence number. A rejected record is left unmodified and must not be decoded.
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

// Half-open record range [start, end) within a chunk buffer.
struct SliceRange {
    uint64_t start;
    uint64_t end;
};

// Partition records into ordered, nonempty ranges and wait for all workers.
template <typename Function>
unsigned ForEachRange(uint64_t total, unsigned threadCount, Function body) {
    if (total == 0) {
        return 0;
    }
    if (threadCount == 1) {
        body(0, SliceRange{0, total});
        return 1;
    }
    uint64_t perThread = (total + threadCount - 1) / threadCount;
    std::vector<std::thread> workers;
    for (unsigned index = 0; index < threadCount; index++) {
        uint64_t start = static_cast<uint64_t>(index) * perThread;
        if (start >= total) {
            break;
        }
        uint64_t end = (std::min)(start + perThread, total);
        workers.emplace_back([&body, index, start, end]() { body(index, SliceRange{start, end}); });
    }
    for (auto& worker : workers) {
        worker.join();
    }
    return static_cast<unsigned>(workers.size());
}

struct ParsedEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint32_t fileAttributes;
    uint16_t flags;
    uint16_t sequenceNumber;
    uint16_t parentSequenceNumber;
    int64_t size;
    int64_t modifiedTime;
    const WCHAR* name;
    uint16_t nameLength;
};

// Per-worker accumulator. Uses std::vector so growth is exception-safe;
// the merge step copies entries and strings out.
struct SliceResult {
    std::vector<MftCompactEntry> entries;
    std::vector<uint16_t> strings;
    // Allocated records this worker passed over because their fixup was invalid.
    uint64_t invalidFixupRecords = 0;

    void append(const ParsedEntry& entry) {
        uint64_t stringOffset = strings.size();
        if (entry.nameLength > 0 && entry.name != nullptr) {
            const auto* src = reinterpret_cast<const uint16_t*>(entry.name);
            strings.insert(strings.end(), src, src + entry.nameLength);
        }
        const MftCompactEntry compact{
            entry.recordNumber,   entry.parentRecordNumber,  stringOffset, entry.fileAttributes,
            entry.flags,          entry.nameLength,          entry.size,   entry.modifiedTime,
            entry.sequenceNumber, entry.parentSequenceNumber};
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

// Read-only inputs that steer record scanning, threaded through the scan pipeline as one
// const&. includeFreed also emits base records whose in-use bit is clear. control is the
// caller's cancellation flag and thread allowance (null when the caller supplied none).
// rejectInvalidFixup fails the parse on an allocated record whose fixup is invalid.
struct ScanContext {
    bool includeFreed = false;
    uint64_t totalRecords = 0;
    ParseGeometry geometry{};
    const MftParseControl* control = nullptr;
    bool rejectInvalidFixup = false;
};

bool AppendSlice(CompactOutput& output, const SliceResult& slice, MftMessageChar* errorMessage);
void ProcessRecordSlice(uint8_t* buffer, SliceRange range, uint64_t recordBase, SliceResult* slice,
                        const ScanContext& scan);

using ReadChunkFn = uint64_t (*)(void* context, uint8_t* targetBuffer, double& ioMs);
using InputIncompleteFn = bool (*)(const void* context);

// Where a parse reads its records and how far it trusts them.
struct ParseSource {
    ReadChunkFn readChunk = nullptr;
    void* context = nullptr;
    uint64_t totalRecords = 0;
    ParseGeometry geometry{};
    // Asked once the last chunk is read: true when the input ended early, failed or changed
    // size, which fails the parse. Null for a source that cannot tell.
    InputIncompleteFn incomplete = nullptr;
    // True for a file, which is untrusted: an allocated record with an invalid fixup fails the
    // parse. A volume only passes over such a record.
    bool rejectInvalidFixup = false;
};

// What the caller asked of one parse. control may be null (every processor, never cancelled).
// includeFreed also emits base records whose in-use bit is clear.
struct ParseRequest {
    bool includeFreed = false;
    uint32_t bufferSizeRecords = 0;
    const MftParseControl* control = nullptr;
    MftProgressCallback callback = nullptr;
    void* progressContext = nullptr;
};

// A zeroed result carrying the ABI version and entry stride, and message when one is given.
// Null when the allocation fails.
MftParseResult* CreateParseResult(const wchar_t* message = nullptr);

// A parse stopped by control->cancelRequested returns a result with no entries, cancelled set
// to 1 and errorMessage "Parse cancelled". A parse whose source reports incomplete input, or
// that rejects an invalid fixup, returns a result with invalidInput set to 1.
MftParseResult* ParseMFTImpl(const ParseSource& source, const ParseRequest& request);
