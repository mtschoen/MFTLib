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

// Run arithmetic for lengths and offsets read from an untrusted volume. TryClusterByteOffset is false
// when the byte offset overflows 64 bits or leaves the signed range a file offset can take.
// TryMeasureRuns is false when any run's length or extent overflows, starts before the volume or ends
// beyond that range, or when the lengths sum past 64 bits; every other run computation here (the
// run byte length, disk offsets inside a run, the running sum) is plain arithmetic that holds only for
// runs it has accepted.
bool TryClusterByteOffset(uint64_t cluster, uint32_t bytesPerCluster, uint64_t* byteOffset);
bool TryMeasureRuns(const std::vector<DataRun>& runs, uint32_t bytesPerCluster, uint64_t* totalBytes);

// The runs of a non-resident attribute. Empty when the attribute is resident, its header or mapping
// pairs do not fit its length, a run field is wider than eight bytes, or a run is cut off by the end
// of the attribute: a run list is complete or not used.
std::vector<DataRun> ParseDataRuns(const ATTRIBUTE_RECORD_HEADER* attr);

// Steps through a record's attributes without leaving the record. *offset starts at the record's
// FirstAttributeOffset and advances past each attribute returned. Malformed when an attribute header
// or its declared length does not fit inside the record.
enum class AttributeStep : uint8_t { Attribute, End, Malformed };
AttributeStep NextAttribute(uint8_t* record, uint32_t recordSize, size_t* offset, PATTRIBUTE_RECORD_HEADER* attribute);

// The first attribute of the given type, or null when the record has none. malformed is set when the
// walk met an attribute that does not fit the record before it reached the end marker or the type.
struct AttributeSearch {
    PATTRIBUTE_RECORD_HEADER attribute;
    bool malformed;
};
AttributeSearch FindAttribute(uint8_t* record, uint32_t recordSize, ATTRIBUTE_TYPE_CODE type);

// Off Windows the handle is a file descriptor, so an image file stands in for the volume. The read
// takes its position from the offset alone and leaves no file position behind.
BOOL Read(HANDLE handle, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead);

// The first fileSize bytes of a non-resident attribute, read in whole clusters, or null when the
// attribute is malformed, its runs are out of range or shorter than fileSize, or a read fails.
// *outSize is the number of bytes in the returned buffer, all of them read.
uint8_t* ReadNonResidentData(HANDLE volumeHandle, const ATTRIBUTE_RECORD_HEADER* attr, uint32_t bytesPerCluster,
                             uint64_t* outSize);

// mftRuns must already be validated, with a cumulative size TryMeasureRuns accepted: the lookup does
// not remeasure the list, so it stays linear in the runs it visits. False when the record is not
// covered by them or its read fails.
bool ReadMFTRecord(HANDLE volumeHandle, const std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster,
                   ParseGeometry geometry, uint8_t* buffer, uint64_t recordNumber);
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
    // What a parse whose incomplete callback answered true reports: the error message, and
    // whether the failure counts as rejected content (invalidInput) rather than an I/O fault.
    const wchar_t* incompleteMessage = L"The input could not be read completely.";
    bool incompleteIsInvalidInput = false;
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
// to 1 and errorMessage "Parse cancelled". A parse whose source reports incomplete input returns a
// result with no entries and incompleteMessage as its errorMessage; invalidInput is set to 1 when
// the source says its content was rejected. A parse that rejects an invalid fixup stops at that
// chunk and keeps the entries parsed before it, with errorMessage saying why.
MftParseResult* ParseMFTImpl(const ParseSource& source, const ParseRequest& request);
