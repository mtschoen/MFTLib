#pragma once
#include <cstdint>

#pragma pack(push, 1)

struct MftFileEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint16_t flags;           // bit 0 = in use, bit 1 = directory
    uint16_t fileNameLength;  // wchar_t count (excluding null terminator)
    wchar_t  fileName[260];   // MAX_PATH, null-terminated
};

struct MftParseResult {
    uint64_t      totalRecords;
    uint64_t      usedRecords;
    MftFileEntry* entries;        // array, owned by native side
    wchar_t       errorMessage[256];
    // Performance counters (milliseconds)
    double        ioTimeMs;       // time spent in ReadFile calls
    double        fixupTimeMs;    // time spent applying USA fixups
    double        parseTimeMs;    // time spent scanning attributes
    double        totalTimeMs;    // wall clock for entire ParseMFTRecords
};

#pragma pack(pop)
