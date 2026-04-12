#pragma once
#include <cstdint>

#pragma pack(push, 1)

struct MftFileEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint16_t flags;           // bit 0 = in use, bit 1 = directory
    uint16_t fileNameLength;  // wchar_t count (excluding null terminator)
    uint32_t fileAttributes;  // Win32 FILE_ATTRIBUTE_* flags from $STANDARD_INFORMATION (preferred) or $FILE_NAME (fallback)
    wchar_t  fileName[260];   // MAX_PATH, null-terminated
};

struct MftPathEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint16_t flags;
    uint16_t pathLength;      // wchar_t count
    uint32_t fileAttributes;  // Win32 FILE_ATTRIBUTE_* flags from $STANDARD_INFORMATION (preferred) or $FILE_NAME (fallback)
    wchar_t  path[1024];      // full resolved path from root
};

struct MftParseResult {
    uint64_t       totalRecords;
    uint64_t       usedRecords;
    MftFileEntry*  entries;        // array, owned by native side (unfiltered or filtered without paths)
    wchar_t        errorMessage[256];
    // Performance counters (milliseconds)
    double         ioTimeMs;       // time spent in ReadFile calls
    double         fixupTimeMs;    // time spent applying USA fixups
    double         parseTimeMs;    // time spent scanning attributes
    double         totalTimeMs;    // wall clock for entire ParseMFTRecords
    MftPathEntry*  pathEntries;    // set when path resolution is requested (matchFlags & 4)
};

struct UsnJournalInfo {
    uint64_t journalId;
    int64_t  firstUsn;
    int64_t  nextUsn;
    int64_t  lowestValidUsn;
    int64_t  maxUsn;
    uint64_t maximumSize;
    uint64_t allocationDelta;
    wchar_t  errorMessage[256];
};

struct UsnJournalEntry {
    uint64_t recordNumber;        // file reference number (lower 48 bits)
    uint64_t parentRecordNumber;  // parent file reference number (lower 48 bits)
    int64_t  usn;                 // USN of this record
    int64_t  timestamp;           // FILETIME as int64
    uint32_t reason;              // USN_REASON_* flags
    uint32_t fileAttributes;      // Win32 FILE_ATTRIBUTE_* flags
    uint16_t fileNameLength;      // wchar_t count
    wchar_t  fileName[260];       // MAX_PATH, null-terminated
};

struct UsnJournalResult {
    uint64_t         entryCount;
    UsnJournalEntry* entries;        // array, owned by native side
    int64_t          nextUsn;        // cursor for next read
    uint64_t         journalId;      // journal ID for staleness detection
    wchar_t          errorMessage[256];
};

#pragma pack(pop)
