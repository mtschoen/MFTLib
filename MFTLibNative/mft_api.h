#pragma once
#include <cstddef>
#include <cstdint>

#ifndef EXPORT
    #ifdef _WIN32
        #define EXPORT __declspec(dllexport)
    #else
        #define EXPORT __attribute__((visibility("default")))
    #endif
#endif

constexpr uint32_t MFT_NATIVE_ABI_VERSION = 7;

// One UTF-16 code unit of a parse error message. wchar_t is 16 bits on Windows and 32 bits
// elsewhere, so the parse structs declare their message buffers in this type and keep one
// layout on every platform.
#ifdef _WIN32
using MftMessageChar = wchar_t;
#else
using MftMessageChar = char16_t;
#endif

// Parser-synthesized, not an on-disk NTFS record flag. The flags field carries the
// raw FILE_RECORD_SEGMENT_HEADER flags in the low bits; the parser sets this top
// bit when a non-directory record has
// no unnamed $DATA attribute in its base segment, so the size column holds zero
// because the size is unknown rather than because the file is empty.
constexpr uint16_t MFT_ENTRY_FLAG_SIZE_UNKNOWN = 0x8000;

using MftProgressCallback = void (*)(uint64_t recordsScanned, uint64_t totalRecords, double elapsedMs, void* context);

// Caller-owned, kept in place for the whole parse call. The caller may write either field while
// the parse runs; the parser only reads them, atomically. cancelRequested nonzero asks the parse
// to stop. parseThreadAllowance is the thread count for each chunk, read at the start of the
// chunk: 0 means every processor, any other value is clamped to [1, processors].
struct MftParseControl {
    int32_t cancelRequested;
    int32_t parseThreadAllowance;
};

static_assert(sizeof(MftParseControl) == 8, "MftParseControl is two 32-bit fields");
static_assert(offsetof(MftParseControl, cancelRequested) == 0, "cancelRequested leads MftParseControl");
static_assert(offsetof(MftParseControl, parseThreadAllowance) == 4, "parseThreadAllowance follows cancelRequested");
static_assert(alignof(MftParseControl) == alignof(int32_t),
              "MftParseControl fields are shared 32-bit loads and need natural alignment");

#pragma pack(push, 1)

struct MftCompactEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint64_t stringOffset;  // UTF-16 code units from the selected pool base
    uint32_t fileAttributes;
    uint16_t flags;
    uint16_t stringLength;    // UTF-16 code units; zero is valid
    int64_t size;             // bytes; zero for a directory or a size-unknown record
    int64_t modifiedTime;     // FILETIME, 100-nanosecond intervals since 1601-01-01 UTC
    uint16_t sequenceNumber;  // NTFS record sequence; combined with recordNumber it is the file reference
    // Sequence the record's chosen name attribute stores in its parent reference; the parent record
    // number is parentRecordNumber. Emitted for every entry, whatever the scan flags.
    uint16_t parentSequenceNumber;
};

static_assert(sizeof(MftCompactEntry) == 52, "MftCompactEntry is the 52-byte packed row the managed side reads");
static_assert(offsetof(MftCompactEntry, parentSequenceNumber) == 50,
              "parentSequenceNumber is the last field of MftCompactEntry");

struct MftParseResult {
    uint64_t totalRecords;
    uint64_t usedRecords;
    MftCompactEntry* entries;
    // NOLINTNEXTLINE(modernize-avoid-c-arrays)
    MftMessageChar errorMessage[256];
    // Performance counters (milliseconds)
    double ioTimeMs;
    double fixupTimeMs;
    double parseTimeMs;
    double totalTimeMs;
    uint16_t* entryStrings;
    uint64_t entryStringUnits;
    uint32_t abiVersion;
    uint32_t entryStride;
    uint32_t cancelled;  // 1 when the parse stopped because MftParseControl::cancelRequested was set
    // 1 when the content of a file input was rejected and errorMessage says why: an empty file, an
    // unsupported record size, a partial final record, an invalid fixup on an allocated record, or
    // input that could not be read completely.
    uint32_t invalidInput;
    // Allocated FILE records a volume scan passed over because their fixup was invalid. They have
    // no row in the result. Always 0 for a file input, where one such record fails the parse.
    uint64_t invalidFixupRecords;
};

// An opened MFT dump file: created by OpenMftDumpInput, read by ParseMftDumpInput, released by
// CloseMftDumpInput. Opaque to the caller.
struct MftDumpInput;

// What OpenMftDumpInput found. errorMessage is empty exactly when the open returned an input;
// invalidInput is 1 when the file opened but its content was rejected.
struct MftDumpInputInfo {
    uint64_t lengthBytes;
    uint32_t recordSize;
    uint32_t invalidInput;
    // NOLINTNEXTLINE(modernize-avoid-c-arrays)
    MftMessageChar errorMessage[256];
};

struct UsnJournalInfo {
    uint64_t journalId;
    int64_t firstUsn;
    int64_t nextUsn;
    int64_t lowestValidUsn;
    int64_t maxUsn;
    uint64_t maximumSize;
    uint64_t allocationDelta;
    // NOLINTNEXTLINE(modernize-avoid-c-arrays)
    wchar_t errorMessage[256];
};

struct UsnJournalEntry {
    uint64_t recordNumber;        // file reference number (lower 48 bits)
    uint64_t parentRecordNumber;  // parent file reference number (lower 48 bits)
    int64_t usn;                  // USN of this record
    int64_t timestamp;            // FILETIME as int64
    uint32_t reason;              // USN_REASON_* flags
    uint32_t fileAttributes;      // Win32 FILE_ATTRIBUTE_* flags
    uint16_t fileNameLength;      // wchar_t count
    // NOLINTNEXTLINE(modernize-avoid-c-arrays)
    wchar_t fileName[260];  // MAX_PATH, null-terminated
    uint16_t sequenceNumber;
};

struct UsnJournalResult {
    uint64_t entryCount;
    UsnJournalEntry* entries;  // array, owned by native side
    int64_t nextUsn;           // cursor for next read
    uint64_t journalId;        // journal ID for staleness detection
    // NOLINTNEXTLINE(modernize-avoid-c-arrays)
    wchar_t errorMessage[256];
};

#pragma pack(pop)

extern "C" {
EXPORT uint32_t GetMftNativeAbiVersion();
}
