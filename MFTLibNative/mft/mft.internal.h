#pragma once

#include <algorithm>
#include <cstdint>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../mft_api.h"
#include "../core/exception_barrier.h"
#include "../core/platform.h"

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
inline constexpr wchar_t kIncompleteVolumeReadMessage[] = L"The volume could not be read completely.";

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
std::vector<DataRun> ParseDataRuns(const ATTRIBUTE_RECORD_HEADER* attribute);

constexpr size_t kMinimumFirstAttributeOffset = 42;
constexpr size_t kResidentAttributeHeaderSize = 0x18;

// Steps through a record's attributes without leaving the record. *offset starts at the record's
// FirstAttributeOffset and advances past each attribute returned. Malformed when an attribute header
// or its declared length does not fit inside the record.
enum class AttributeStep : uint8_t { Attribute, End, Malformed };
AttributeStep NextAttribute(uint8_t* record, uint32_t recordSize, size_t* offset, PATTRIBUTE_RECORD_HEADER* attribute);

// The first attribute of the given type, or null when the record has none. malformed is set when the
// walk met an attribute that does not fit the record before it reached the end marker or the type.
struct AttributeSearch {
    PATTRIBUTE_RECORD_HEADER attribute = nullptr;
    bool malformed = false;
};
AttributeSearch FindAttribute(uint8_t* record, uint32_t recordSize, ATTRIBUTE_TYPE_CODE type);

// How reads of a raw volume handle must be shaped: offsets and lengths are multiples of sectorBytes
// and destination addresses multiples of memoryBytes. Both are powers of two; 1 asks nothing.
struct VolumeAlignment {
    uint32_t sectorBytes = 1;
    uint32_t memoryBytes = 1;
};

// A volume or image handle with the alignment its reads keep. The handle is borrowed.
struct RawVolume {
    HANDLE handle = nullptr;
    VolumeAlignment alignment{};
};

// The handle with its alignment. On Windows that is the logical sector size for offsets and lengths
// and the larger of the logical and physical sector sizes for addresses, as the storage stack reports
// them (IOCTL_STORAGE_QUERY_PROPERTY, StorageAccessAlignmentProperty); where it does not answer, the
// file system's sector size (FSCTL_GET_NTFS_VOLUME_DATA) for offsets and lengths and, for addresses, the physical
// sector size of the extended data that call returns after it (the logical size when that is absent or implausible);
// where neither answers, nothing.
// Off Windows the handle is a file descriptor read with pread, which asks nothing. The
// SetVolumeAlignmentOverride test hook replaces both values.
RawVolume OpenRawVolume(HANDLE handle);

// Reads count bytes at from into buffer, which may have any address, offset and length: a read whose
// destination, offset or length breaks the volume's alignment reads the whole sectors around it into an
// aligned bounce buffer and copies the asked-for bytes out, and fails when those sectors span more than a
// DWORD or the bounce buffer cannot be allocated. *bytesRead is how many of the asked-for bytes arrived;
// like ReadFile on a file, a read that reaches the end succeeds with fewer. Off Windows the handle is a
// file descriptor, so an image file stands in for the volume. The read takes its position from the
// offset alone and leaves no file position behind.
BOOL Read(const RawVolume& volume, void* buffer, VolumeOffset from, DWORD count, PDWORD bytesRead);

// Reads the first fileSize bytes of a non-resident attribute, in whole clusters, into *data and sets
// *outSize to fileSize. False when the attribute is malformed, its runs are out of range or shorter
// than fileSize, the buffer cannot be allocated, or a read fails.
bool ReadNonResidentData(const RawVolume& volume, const ATTRIBUTE_RECORD_HEADER* attribute, uint32_t bytesPerCluster,
                         mftlib::platform::AlignedBuffer* data, uint64_t* outSize);

// mftRuns must already be validated, with a cumulative size TryMeasureRuns accepted: the lookup does
// not remeasure the list, so it stays linear in the runs it visits. Null on success, otherwise the
// failure message: an uncovered, failed or short read, invalid FILE magic, or invalid fixup.
const wchar_t* ReadMFTRecord(const RawVolume& volume, const std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster,
                             ParseGeometry geometry, uint8_t* buffer, uint64_t recordNumber);

// What a fixed-up record's own attributes say about its name, by the rule the scan applies (mft.records.cpp): the
// first $FILE_NAME that is not DOS-only, null when it holds none, and whether it carries an $ATTRIBUTE_LIST. readable
// is false when a $STANDARD_INFORMATION, a $FILE_NAME or the attribute chain is malformed.
struct RecordName {
    const FILE_NAME* name = nullptr;
    bool attributeListPresent = false;
    bool readable = false;
};
RecordName ScanRecordName(PFILE_RECORD_SEGMENT_HEADER record, ParseGeometry geometry);
}  // namespace detail
}  // namespace mftlib::ntfs

// Half-open record range [start, end) within a chunk buffer.
struct SliceRange {
    uint64_t start;
    uint64_t end;
};

// Partition records into ordered, nonempty ranges and wait for all workers.
template <typename Function>
unsigned ForEachRange(const uint64_t total, const unsigned threadCount, Function body) {
    if (total == 0) {
        return 0;
    }
    if (threadCount == 1) {
        body(0, SliceRange{0, total});
        return 1;
    }
    const uint64_t perThread = (total + threadCount - 1) / threadCount;
    std::vector failures(threadCount, NativeFailure::None);
    std::vector<std::thread> workers;
    workers.reserve(threadCount);
    const NativeScopeExit joined([&] {
        for (auto& worker : workers) {
            if (worker.joinable()) {
                worker.join();
            }
        }
    });
    for (unsigned index = 0; index < threadCount; index++) {
        uint64_t start = static_cast<uint64_t>(index) * perThread;
        if (start >= total) {
            break;
        }
        uint64_t end = (std::min)(start + perThread, total);
        workers.push_back(
            StartNativeThread([&body, index, start, end] { body(index, SliceRange{start, end}); }, failures[index]));
    }
    for (auto& worker : workers) {
        worker.join();
    }
    for (const auto& failure : failures) {
        ThrowNativeFailure(failure);
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

struct ExtensionDataSize {
    uint64_t baseRecordNumber;
    uint16_t baseSequenceNumber;
    int64_t size;
};

// What a row takes from a $FILE_NAME: the name, its parent, and the attributes and time a record
// without $STANDARD_INFORMATION falls back to.
struct FileNameFacts {
    uint64_t parentRecordNumber = 0;
    int64_t modifiedTime = 0;
    uint32_t fileAttributes = 0;
    uint16_t parentSequenceNumber = 0;
    const WCHAR* name = nullptr;
    uint16_t nameLength = 0;
};

// A non-DOS $FILE_NAME an in-use extension record carries for its base. NTFS moves a base's names
// into extension records when the base fills up, so the base may hold none of its own. The name is
// copied out because the record buffer is reused by the next read.
struct ExtensionName {
    uint64_t baseRecordNumber = 0;
    uint64_t extensionRecordNumber = 0;
    uint16_t baseSequenceNumber = 0;
    FileNameFacts facts{};
    std::vector<WCHAR> name;
};

// An eligible base record whose own attributes hold no non-DOS $FILE_NAME. Its row is complete except
// for the name and parent, which an extension record may supply once every record is scanned.
struct NamelessBase {
    ParsedEntry entry{};
    bool standardInformationPresent = false;
};

// Per-worker accumulator. Uses std::vector so growth is exception-safe;
// the merge step copies entries and strings out.
struct SliceResult {
    std::vector<MftCompactEntry> entries;
    std::vector<uint16_t> strings;
    std::vector<ExtensionDataSize> extensionDataSizes;
    std::vector<ExtensionName> extensionNames;
    std::vector<NamelessBase> namelessBases;
    // Allocated records this worker passed over because their fixup was invalid.
    uint64_t invalidFixupRecords = 0;

    void append(const ParsedEntry& entry) {
        const uint64_t stringOffset = strings.size();
        if (entry.nameLength > 0 && entry.name != nullptr) {
            const auto* source = reinterpret_cast<const uint16_t*>(entry.name);
            strings.insert(strings.end(), source, source + entry.nameLength);
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
    std::vector<ExtensionDataSize> extensionDataSizes;
    std::vector<ExtensionName> extensionNames;
    std::vector<NamelessBase> namelessBases;
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
// Keeps what a slice's extension records said about their bases, for the fix-ups after the last chunk.
void KeepExtensionFacts(CompactOutput& output, const SliceResult& slice);
// The rows of the nameless bases an in-use extension of the same base sequence names, in record order.
// Of several naming extensions the lowest-numbered one wins.
SliceResult NameBasesFromExtensions(CompactOutput& output);
// Appends NameBasesFromExtensions to the collected entries and keeps them in record order. False when
// the entry array or string pool cannot grow (error set).
bool AppendNamedBases(CompactOutput& output, MftMessageChar* errorMessage);
void ApplyExtensionDataSizes(CompactOutput& output);
void ProcessRecordSlice(uint8_t* buffer, SliceRange range, uint64_t recordBase, SliceResult* slice,
                        const ScanContext& scan);

using ReadChunkFunction = uint64_t (*)(void* context, uint8_t* targetBuffer, double& ioMs);
using InputIncompleteFunction = bool (*)(const void* context);

// Where a parse reads its records and how far it trusts them.
struct ParseSource {
    ReadChunkFunction readChunk = nullptr;
    void* context = nullptr;
    uint64_t totalRecords = 0;
    ParseGeometry geometry{};
    // Asked once the last chunk is read: true when the input ended early, failed or changed
    // size, which fails the parse. Null for a source that cannot tell.
    InputIncompleteFunction incomplete = nullptr;
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
    void* callbackContext = nullptr;
    // When set, each chunk's slices go to the sink as soon as the chunk is parsed and the result
    // carries no entries; the extension data sizes follow the last chunk.
    const MftParseSink* sink = nullptr;
};

// A zeroed result carrying the entry stride, and message when one is given.
// Null when the allocation fails.
MftParseResult* CreateParseResult(const wchar_t* message = nullptr);

// A parse stopped by control->cancelRequested returns a result with no entries, cancelled set
// to 1 and errorMessage "Parse cancelled". A parse whose source reports incomplete input returns a
// result with no entries and incompleteMessage as its errorMessage; invalidInput is set to 1 when
// the source says its content was rejected. A parse that rejects an invalid fixup stops at that
// chunk and keeps the entries parsed before it, with errorMessage saying why.
MftParseResult* ParseMFTImpl(const ParseSource& source, const ParseRequest& request);
