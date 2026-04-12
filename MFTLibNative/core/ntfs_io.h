#pragma once

#include <vector>
#include "../ntfs.h"

struct DataRun {
    int64_t clusterOffset;
    uint64_t clusterCount;
};

BOOL Read(HANDLE handle, void* buffer, uint64_t from, DWORD count, PDWORD bytesRead);
bool ApplyFixup(uint8_t* record, uint32_t recordSize);
std::vector<DataRun> ParseDataRuns(PATTRIBUTE_RECORD_HEADER attr);
uint8_t* ReadNonResidentData(HANDLE volumeHandle, PATTRIBUTE_RECORD_HEADER attr, uint32_t bytesPerCluster, uint64_t* outSize);
bool ReadMFTRecord(HANDLE volumeHandle, std::vector<DataRun>& mftRuns, uint32_t bytesPerCluster, uint64_t recordNumber, uint8_t* buffer);
PATTRIBUTE_RECORD_HEADER FindAttribute(uint8_t* record, ATTRIBUTE_TYPE_CODE type);
