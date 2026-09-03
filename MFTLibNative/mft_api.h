#pragma once
#include <cstdint>

#ifndef EXPORT
    #ifdef _WIN32
        #define EXPORT __declspec(dllexport)
    #else
        #define EXPORT __attribute__((visibility("default")))
    #endif
#endif

constexpr uint32_t MFT_NATIVE_ABI_VERSION = 4;

// Parser-synthesized, not an on-disk NTFS record flag. The flags field carries the
// raw FILE_RECORD_SEGMENT_HEADER flags, whose defined bits are 0x0001 (in use) and
// 0x0002 (directory); the parser sets this top bit when a non-directory record has
// no unnamed $DATA attribute in its base segment, so the size column holds zero
// because the size is unknown rather than because the file is empty.
constexpr uint16_t MFT_ENTRY_FLAG_SIZE_UNKNOWN = 0x8000;

constexpr uint32_t MATCH_FLAG_NONE = 0;
constexpr uint32_t MATCH_FLAG_EXACT_MATCH = 1;
constexpr uint32_t MATCH_FLAG_CONTAINS = 2;
constexpr uint32_t MATCH_FLAG_RESOLVE_PATHS = 4;

enum class MftScanPhase : uint8_t {
    Parsing = 0,
    ResolvingPaths = 1,
};

using MftProgressCallback = void (*)(MftScanPhase phase, uint64_t recordsScanned, uint64_t totalRecords,
                                     double elapsedMs, void* context);

#pragma pack(push, 1)

struct MftCompactEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint64_t stringOffset;  // UTF-16 code units from the selected pool base
    uint32_t fileAttributes;
    uint16_t flags;
    uint16_t stringLength;  // UTF-16 code units; zero is valid
    int64_t size;           // bytes; zero for a directory or a size-unknown record
    int64_t modifiedTime;   // FILETIME, 100-nanosecond intervals since 1601-01-01 UTC
};

struct MftParseResult {
    uint64_t totalRecords;
    uint64_t usedRecords;
    MftCompactEntry* entries;
    // NOLINTNEXTLINE(modernize-avoid-c-arrays)
    wchar_t errorMessage[256];
    // Performance counters (milliseconds)
    double ioTimeMs;
    double fixupTimeMs;
    double parseTimeMs;
    double totalTimeMs;
    MftCompactEntry* pathEntries;
    uint16_t* entryStrings;
    uint64_t entryStringUnits;
    uint16_t* pathStrings;
    uint64_t pathStringUnits;
    uint32_t abiVersion;
    uint32_t entryStride;
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
