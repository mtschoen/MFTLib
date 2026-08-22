#pragma once
#include <cstdint>

#ifndef EXPORT
    #ifdef _WIN32
        #define EXPORT __declspec(dllexport)
    #else
        #define EXPORT __attribute__((visibility("default")))
    #endif
#endif

constexpr uint32_t MFT_NATIVE_ABI_VERSION = 2;

using MftProgressCallback = void (*)(uint64_t recordsScanned, uint64_t totalRecords, double elapsedMs, void* context);

#pragma pack(push, 1)

struct MftCompactEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint64_t stringOffset;  // UTF-16 code units from the selected pool base
    uint32_t fileAttributes;
    uint16_t flags;
    uint16_t stringLength;  // UTF-16 code units; zero is valid
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
