#include "pch.h"

#include <chrono>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/ntfs_io.h"

static bool FileNameMatches(const wchar_t* name, uint8_t nameLen,
                            const wchar_t* filter, uint16_t filterLen,
                            uint32_t matchFlags) {
    if (matchFlags & 1) {
        if (nameLen != filterLen) return false;
        return _wcsnicmp(name, filter, nameLen) == 0;
    }
    if (matchFlags & 2) {
        if (filterLen > nameLen) return false;
        for (uint16_t i = 0; i <= nameLen - filterLen; i++) {
            if (_wcsnicmp(name + i, filter, filterLen) == 0)
                return true;
        }
        return false;
    }
    return false;
}

struct PathLookup {
    uint64_t* parents;
    uint8_t*  nameLens;
    uint32_t* nameOffsets;
    wchar_t*  namePool;
    uint64_t  namePoolUsed;
    uint64_t  namePoolCapacity;

    bool init(uint64_t totalRecords) {
        parents = (uint64_t*)calloc((size_t)totalRecords, sizeof(uint64_t));
        nameLens = (uint8_t*)calloc((size_t)totalRecords, sizeof(uint8_t));
        nameOffsets = (uint32_t*)calloc((size_t)totalRecords, sizeof(uint32_t));
        namePoolCapacity = totalRecords * 32;
        namePool = (wchar_t*)malloc((size_t)namePoolCapacity * sizeof(wchar_t));
        namePoolUsed = 0;
        return parents && nameLens && nameOffsets && namePool;
    }

    void storeName(uint64_t recordIndex, uint64_t parent, const wchar_t* name, uint8_t nameLen) {
        parents[recordIndex] = parent;
        uint64_t offset = (uint64_t)InterlockedExchangeAdd64(
            (volatile LONG64*)&namePoolUsed, (LONG64)nameLen);
        if (offset + nameLen > namePoolCapacity) return;
        nameOffsets[recordIndex] = (uint32_t)offset;
        wmemcpy(namePool + offset, name, nameLen);
        nameLens[recordIndex] = nameLen;
    }

    void cleanup() const {
        free(parents);
        free(nameLens);
        free(nameOffsets);
        free(namePool);
    }
};

static uint16_t ResolvePath(uint64_t recordIndex, PathLookup& lookup, uint64_t totalRecords,
                            wchar_t* pathBuf, uint16_t pathBufSize) {
    struct Component { const wchar_t* name; uint8_t len; };
    Component stack[128] = {};
    int depth = 0;
    uint64_t current = recordIndex;
    uint64_t visited[128] = {};
    int visitCount = 0;

    while (current != 5 && current < totalRecords && depth < 128) {
        bool cycle = false;
        for (int v = 0; v < visitCount; v++)
            if (visited[v] == current) { cycle = true; break; }
        if (cycle) break;
        visited[visitCount++] = current;

        if (lookup.nameLens[current] == 0) break;
        stack[depth].name = lookup.namePool + lookup.nameOffsets[current];
        stack[depth].len = lookup.nameLens[current];
        depth++;
        current = lookup.parents[current];
    }

    uint16_t pos = 0;
    for (int i = depth - 1; i >= 0; i--) {
        if (pos + stack[i].len + 1 >= pathBufSize) break;
        if (pos > 0) pathBuf[pos++] = L'\\';
        wmemcpy(pathBuf + pos, stack[i].name, stack[i].len);
        pos += stack[i].len;
    }
    pathBuf[pos] = L'\0';
    return pos;
}

struct SliceResult {
    MftFileEntry* entries;
    uint64_t count;
    uint64_t capacity;

    void init(uint64_t cap) {
        entries = (MftFileEntry*)malloc((size_t)cap * sizeof(MftFileEntry));
        count = 0;
        capacity = cap;
    }
    void cleanup() const { free(entries); }
};

static void ProcessRecordSlice(
        uint8_t* buffer, uint64_t startIdx, uint64_t endIdx, uint64_t recordBase,
        SliceResult* slice,
        const wchar_t* filter, uint16_t filterLen, uint32_t matchFlags,
        PathLookup* lookup, uint64_t totalRecords) {

    for (uint64_t i = startIdx; i < endIdx; i++) {
        uint64_t recordIndex = recordBase + i;
        auto* recPtr = buffer + FILE_RECORD_SIZE * i;
        auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;

        if (rec->MultiSectorHeader.Magic != 0x454C4946) continue;
        if (!(rec->Flags & 0x0001)) continue;

        uint64_t baseRef = (uint64_t)rec->BaseFileRecordSegment.SegmentNumberLowPart |
                           ((uint64_t)rec->BaseFileRecordSegment.SegmentNumberHighPart << 32);
        if (baseRef != 0) continue;

        auto* attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)rec + rec->FirstAttributeOffset);
        uint32_t siAttributes = 0;
        bool sawStandardInformation = false;
        while ((uint8_t*)attr - (uint8_t*)rec < FILE_RECORD_SIZE) {
            if (attr->TypeCode == EndMarker || attr->RecordLength == 0) break;

            if (attr->TypeCode == StandardInformation && attr->FormCode == 0) {
                auto* siValue = (uint8_t*)attr + attr->Form.Resident.ValueOffset;
                siAttributes = *(uint32_t*)(siValue + 32);
                sawStandardInformation = true;
            }
            else if (attr->TypeCode == FileName && attr->FormCode == 0) {
                auto* fn = (PFILE_NAME)((uint8_t*)attr + attr->Form.Resident.ValueOffset);
                if (fn->Flags != 2) {
                    uint64_t parent = (uint64_t)fn->ParentDirectory.SegmentNumberLowPart |
                                      ((uint64_t)fn->ParentDirectory.SegmentNumberHighPart << 32);

                    if (lookup && recordIndex < totalRecords) {
                        lookup->storeName(recordIndex, parent, fn->FileName, fn->FileNameLength);
                    }

                    if (filter && !FileNameMatches(fn->FileName, fn->FileNameLength, filter, filterLen, matchFlags))
                        break;

                    if (slice->count >= slice->capacity) {
                        slice->capacity *= 2;
                        slice->entries = (MftFileEntry*)realloc(slice->entries,
                            (size_t)slice->capacity * sizeof(MftFileEntry));
                    }

                    auto& entry = slice->entries[slice->count];
                    memset(&entry, 0, sizeof(MftFileEntry));
                    entry.recordNumber = recordIndex;
                    entry.parentRecordNumber = parent;
                    entry.flags = rec->Flags;
                    entry.fileNameLength = fn->FileNameLength;
                    entry.fileAttributes = sawStandardInformation ? siAttributes : fn->FileAttributes;
                    uint16_t copyLen = min((uint16_t)fn->FileNameLength, (uint16_t)259);
                    wmemcpy_s(entry.fileName, 260, fn->FileName, copyLen);

                    slice->count++;
                    break;
                }
            }
            attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attr + attr->RecordLength);
        }
    }
}

static void ProcessRecordBatch(
        uint8_t* buffer,
        uint64_t filesToLoad, uint64_t& recordIndex,
        MftParseResult* result, uint64_t& usedCount, uint64_t& capacity,
        const wchar_t* filter, uint16_t filterLen, uint32_t matchFlags,
        PathLookup* lookup, uint64_t totalRecords) {

    for (uint64_t i = 0; i < filesToLoad; i++, recordIndex++) {
        auto* recPtr = buffer + FILE_RECORD_SIZE * i;
        auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;

        if (rec->MultiSectorHeader.Magic != 0x454C4946) continue;
        if (!(rec->Flags & 0x0001)) continue;

        uint64_t baseRef = (uint64_t)rec->BaseFileRecordSegment.SegmentNumberLowPart |
                           ((uint64_t)rec->BaseFileRecordSegment.SegmentNumberHighPart << 32);
        if (baseRef != 0) continue;

        auto* attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)rec + rec->FirstAttributeOffset);
        uint32_t siAttributes = 0;
        bool sawStandardInformation = false;
        while ((uint8_t*)attr - (uint8_t*)rec < FILE_RECORD_SIZE) {
            if (attr->TypeCode == EndMarker || attr->RecordLength == 0) break;

            if (attr->TypeCode == StandardInformation && attr->FormCode == 0) {
                auto* siValue = (uint8_t*)attr + attr->Form.Resident.ValueOffset;
                siAttributes = *(uint32_t*)(siValue + 32);
                sawStandardInformation = true;
            }
            else if (attr->TypeCode == FileName && attr->FormCode == 0) {
                auto* fn = (PFILE_NAME)((uint8_t*)attr + attr->Form.Resident.ValueOffset);
                if (fn->Flags != 2) {
                    uint64_t parent = (uint64_t)fn->ParentDirectory.SegmentNumberLowPart |
                                      ((uint64_t)fn->ParentDirectory.SegmentNumberHighPart << 32);

                    if (lookup && recordIndex < totalRecords) {
                        lookup->storeName(recordIndex, parent, fn->FileName, fn->FileNameLength);
                    }

                    if (filter && !FileNameMatches(fn->FileName, fn->FileNameLength, filter, filterLen, matchFlags))
                        break;

                    if (usedCount >= capacity) {
                        uint64_t newCapacity = capacity * 2;
                        auto* grown = ShouldFailAlloc() ? nullptr : (MftFileEntry*)realloc(result->entries, (size_t)newCapacity * sizeof(MftFileEntry));
                        if (!grown) {
                            SetErrorMessage(result->errorMessage, L"Failed to grow entry array");
                            result->usedRecords = usedCount;
                            return;
                        }
                        capacity = newCapacity;
                        result->entries = grown;
                    }

                    auto& entry = result->entries[usedCount];
                    memset(&entry, 0, sizeof(MftFileEntry));
                    entry.recordNumber = recordIndex;
                    entry.parentRecordNumber = parent;
                    entry.flags = rec->Flags;
                    entry.fileNameLength = fn->FileNameLength;
                    entry.fileAttributes = sawStandardInformation ? siAttributes : fn->FileAttributes;
                    uint16_t copyLen = min((uint16_t)fn->FileNameLength, (uint16_t)259);
                    memset(entry.fileName, 0, sizeof(entry.fileName));
                    wmemcpy_s(entry.fileName, 260, fn->FileName, copyLen);

                    usedCount++;
                    break;
                }
            }
            attr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)attr + attr->RecordLength);
        }
    }
}

typedef uint64_t (*ReadChunkFn)(void* context, uint8_t* targetBuffer, double& ioMs);

static MftParseResult* ParseMFTImpl(
        ReadChunkFn readChunk, void* readContext,
        uint64_t totalRecords,
        const wchar_t* filter, uint32_t matchFlags,
        uint32_t bufferSizeRecords) {

    auto wallStart = SteadyClock::now();
    double ioMs = 0, fixupMs = 0, parseMs = 0;

    auto* result = ShouldFailAlloc() ? nullptr : (MftParseResult*)calloc(1, sizeof(MftParseResult));
    if (!result) return nullptr;

    result->totalRecords = totalRecords;

    uint16_t filterLen = filter ? (uint16_t)wcslen(filter) : 0;
    bool resolvePaths = (matchFlags & 4) != 0;

    PathLookup lookup = {};
    if (resolvePaths) {
        if (ShouldFailAlloc() || !lookup.init(totalRecords)) {
            SetErrorMessage(result->errorMessage, L"Failed to allocate path lookup");
            return result;
        }
    }

    unsigned numThreads = EffectiveThreadCount();

    const size_t bufSize = (size_t)bufferSizeRecords * FILE_RECORD_SIZE;
    uint8_t* buf[2] = {};
    buf[0] = ShouldFailAlloc() ? nullptr : (uint8_t*)VirtualAlloc(nullptr, bufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    buf[1] = ShouldFailAlloc() ? nullptr : (uint8_t*)VirtualAlloc(nullptr, bufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!buf[0] || !buf[1]) {
        if (buf[0]) VirtualFree(buf[0], 0, MEM_RELEASE);
        if (buf[1]) VirtualFree(buf[1], 0, MEM_RELEASE);
        if (resolvePaths) lookup.cleanup();
        SetErrorMessage(result->errorMessage, L"Failed to allocate I/O buffers");
        return result;
    }

    uint64_t capacity = filter ? 1024 : totalRecords / 4;
    if (capacity < 1024) capacity = 1024;
    result->entries = ShouldFailAlloc() ? nullptr : (MftFileEntry*)malloc((size_t)capacity * sizeof(MftFileEntry));
    if (!result->entries) {
        VirtualFree(buf[0], 0, MEM_RELEASE);
        VirtualFree(buf[1], 0, MEM_RELEASE);
        if (resolvePaths) lookup.cleanup();
        SetErrorMessage(result->errorMessage, L"Failed to allocate entry array");
        return result;
    }

    uint64_t usedCount = 0;
    uint64_t recordIndex = 0;

    uint64_t currentChunkSize = readChunk(readContext, buf[0], ioMs);
    int curBuf = 0;

    while (currentChunkSize > 0) {
        uint64_t nextChunkSize = 0;
        double nextIoMs = 0;
        std::thread ioThread([&]() {
            nextChunkSize = readChunk(readContext, buf[1 - curBuf], nextIoMs);
        });

        uint8_t* buffer = buf[curBuf];

        if (numThreads > 1) {
            uint64_t perThread = (currentChunkSize + numThreads - 1) / numThreads;

            auto fixupStart = SteadyClock::now();
            {
                std::vector<SliceResult> slices(numThreads);
                std::vector<std::thread> workers;
                std::vector<double> threadFixupMs(numThreads, 0.0);
                unsigned actualThreads = 0;

                for (unsigned t = 0; t < numThreads; t++) {
                    uint64_t tStart = t * perThread;
                    uint64_t tEnd = min(tStart + perThread, currentChunkSize);
                    if (tStart >= currentChunkSize) break;
                    actualThreads++;

                    uint64_t initCap = filter ? 64 : (tEnd - tStart) / 4;
                    if (initCap < 64) initCap = 64;
                    slices[t].init(initCap);

                    workers.emplace_back([buffer, tStart, tEnd, t, &slices, &threadFixupMs,
                                          filter, filterLen, matchFlags,
                                          &lookup, resolvePaths, totalRecords, recordIndex]() {
                        auto fStart = SteadyClock::now();
                        for (uint64_t i = tStart; i < tEnd; i++) {
                            auto* recPtr = buffer + FILE_RECORD_SIZE * i;
                            auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;
                            if (rec->MultiSectorHeader.Magic == 0x454C4946)
                                ApplyFixup(recPtr, FILE_RECORD_SIZE);
                        }
                        threadFixupMs[t] = ElapsedMs(fStart, SteadyClock::now());

                        ProcessRecordSlice(buffer, tStart, tEnd, recordIndex,
                                           &slices[t], filter, filterLen, matchFlags,
                                           resolvePaths ? &lookup : nullptr, totalRecords);
                    });
                }
                for (auto& w : workers) w.join();

                auto combinedEnd = SteadyClock::now();
                double totalElapsed = ElapsedMs(fixupStart, combinedEnd);

                double maxFixup = 0;
                for (unsigned t = 0; t < actualThreads; t++)
                    if (threadFixupMs[t] > maxFixup) maxFixup = threadFixupMs[t];
                fixupMs += maxFixup;
                parseMs += totalElapsed - maxFixup;

                for (unsigned t = 0; t < actualThreads; t++) {
                    auto& s = slices[t];
                    if (s.count > 0) {
                        if (usedCount + s.count > capacity) {
                            while (usedCount + s.count > capacity) capacity *= 2;
                            result->entries = (MftFileEntry*)realloc(result->entries,
                                (size_t)capacity * sizeof(MftFileEntry));
                        }
                        memcpy(result->entries + usedCount, s.entries,
                               (size_t)s.count * sizeof(MftFileEntry));
                        usedCount += s.count;
                    }
                    s.cleanup();
                }
            }
        } else {
            auto fixupStart = SteadyClock::now();
            for (uint64_t i = 0; i < currentChunkSize; i++) {
                auto* recPtr = buffer + FILE_RECORD_SIZE * i;
                auto* rec = (PFILE_RECORD_SEGMENT_HEADER)recPtr;
                if (rec->MultiSectorHeader.Magic == 0x454C4946)
                    ApplyFixup(recPtr, FILE_RECORD_SIZE);
            }
            fixupMs += ElapsedMs(fixupStart, SteadyClock::now());

            auto parseStart = SteadyClock::now();
            ProcessRecordBatch(buffer, currentChunkSize, recordIndex,
                               result, usedCount, capacity,
                               filter, filterLen, matchFlags,
                               resolvePaths ? &lookup : nullptr, totalRecords);
            parseMs += ElapsedMs(parseStart, SteadyClock::now());
            if (result->errorMessage[0] != L'\0') {
                ioThread.join();
                break;
            }
        }

        recordIndex += currentChunkSize;

        ioThread.join();
        ioMs += nextIoMs;

        currentChunkSize = nextChunkSize;
        curBuf = 1 - curBuf;
    }

    VirtualFree(buf[0], 0, MEM_RELEASE);
    VirtualFree(buf[1], 0, MEM_RELEASE);

    if (resolvePaths && usedCount > 0) {
        result->pathEntries = (MftPathEntry*)calloc((size_t)usedCount, sizeof(MftPathEntry));
        if (result->pathEntries) {
            for (uint64_t i = 0; i < usedCount; i++) {
                auto& src = result->entries[i];
                auto& dst = result->pathEntries[i];
                dst.recordNumber = src.recordNumber;
                dst.parentRecordNumber = src.parentRecordNumber;
                dst.flags = src.flags;
                dst.fileAttributes = src.fileAttributes;
                dst.pathLength = ResolvePath(src.recordNumber, lookup, totalRecords,
                                            dst.path, 1024);
            }
        }
        lookup.cleanup();
    }

    result->usedRecords = usedCount;
    result->ioTimeMs = ioMs;
    result->fixupTimeMs = fixupMs;
    result->parseTimeMs = parseMs;
    result->totalTimeMs = ElapsedMs(wallStart, SteadyClock::now());
    return result;
}

struct VolumeReadContext {
    HANDLE volumeHandle;
    std::vector<DataRun>* mftRuns;
    uint32_t bytesPerCluster;
    size_t runIndex;
    uint64_t filesRemaining;
    uint64_t positionInBlock;
    uint32_t bufferSizeRecords;
};

static uint64_t VolumeReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* c = (VolumeReadContext*)ctx;
    while (c->filesRemaining == 0) {
        c->runIndex++;
        if (c->runIndex >= c->mftRuns->size()) return 0;
        auto& run = (*c->mftRuns)[c->runIndex];
        c->filesRemaining = run.clusterCount * c->bytesPerCluster / FILE_RECORD_SIZE;
        c->positionInBlock = 0;
    }

    auto& run = (*c->mftRuns)[c->runIndex];
    uint64_t filesToLoad = min(c->filesRemaining, (uint64_t)c->bufferSizeRecords);
    DWORD readBytes;
    auto ioStart = SteadyClock::now();
    if (!Read(c->volumeHandle, targetBuffer,
              (uint64_t)run.clusterOffset * c->bytesPerCluster + c->positionInBlock,
              (DWORD)(filesToLoad * FILE_RECORD_SIZE), &readBytes)) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    c->positionInBlock += filesToLoad * FILE_RECORD_SIZE;
    c->filesRemaining -= filesToLoad;
    return filesToLoad;
}

struct FileReadContext {
    HANDLE hFile;
    uint64_t recordsRemaining;
    uint32_t bufferSizeRecords;
};

static uint64_t FileReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* c = (FileReadContext*)ctx;
    if (c->recordsRemaining == 0) return 0;
    uint64_t filesToLoad = min(c->recordsRemaining, (uint64_t)c->bufferSizeRecords);
    DWORD readBytes;
    auto ioStart = SteadyClock::now();
    if (ShouldFailRead() || !ReadFile(c->hFile, targetBuffer, (DWORD)(filesToLoad * FILE_RECORD_SIZE), &readBytes, nullptr)) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    c->recordsRemaining -= filesToLoad;
    return filesToLoad;
}

extern "C" {
    EXPORT void FreeMftResult(MftParseResult* result) {
        if (result) {
            free(result->entries);
            free(result->pathEntries);
            free(result);
        }
    }

    EXPORT MftParseResult* ParseMFTRecords(HANDLE volumeHandle, const wchar_t* filter, uint32_t matchFlags, uint32_t bufferSizeRecords) {
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (!result) return nullptr;

        if (volumeHandle == INVALID_HANDLE_VALUE) {
            SetErrorMessage(result->errorMessage, L"Volume handle is invalid");
            return result;
        }

        NTFS_BPB bpb;
        DWORD bytesRead;
        if (!Read(volumeHandle, &bpb, 0, 512, &bytesRead) || bytesRead != 512) {
            SetErrorMessage(result->errorMessage, L"Failed to read boot sector. Error: %lu", GetLastError());
            return result;
        }
        if (bpb.name[0] != 'N' || bpb.name[1] != 'T' || bpb.name[2] != 'F' || bpb.name[3] != 'S') {
            SetErrorMessage(result->errorMessage, L"Volume is not NTFS");
            return result;
        }

        uint32_t bytesPerCluster = bpb.bytesPerSector * bpb.sectorsPerCluster;

        uint8_t record0[FILE_RECORD_SIZE];
        if (!Read(volumeHandle, record0, bpb.mftStart * bytesPerCluster, FILE_RECORD_SIZE, &bytesRead)
            || bytesRead != FILE_RECORD_SIZE) {
            SetErrorMessage(result->errorMessage, L"Failed to read MFT record 0");
            return result;
        }

        ApplyFixup(record0, FILE_RECORD_SIZE);

        auto* fileRecord0 = (PFILE_RECORD_SEGMENT_HEADER)record0;
        if (fileRecord0->MultiSectorHeader.Magic != 0x454C4946) {
            SetErrorMessage(result->errorMessage, L"Invalid MFT record 0 magic");
            return result;
        }

        auto* dataAttr = FindAttribute(record0, Data);
        if (!dataAttr) {
            SetErrorMessage(result->errorMessage, L"No Data attribute in MFT record 0");
            return result;
        }
        auto mftRuns = ParseDataRuns(dataAttr);

        auto* attrListAttr = FindAttribute(record0, AttributeList);
        if (attrListAttr) {
            uint8_t* attrListData;
            uint64_t attrListSize = 0;

            if (attrListAttr->FormCode == 1) {
                attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
            } else {
                attrListSize = attrListAttr->Form.Resident.ValueLength;
                attrListData = (uint8_t*)malloc((size_t)attrListSize);
                if (attrListData)
                    memcpy(attrListData, (uint8_t*)attrListAttr + attrListAttr->Form.Resident.ValueOffset, (size_t)attrListSize);
            }

            if (attrListData) {
                std::vector<uint64_t> extensionRecords;
                uint64_t offset = 0;
                while (offset + sizeof(ATTRIBUTE_LIST_ENTRY) <= attrListSize) {
                    auto* entry = (PATTRIBUTE_LIST_ENTRY)(attrListData + offset);
                    if (entry->RecordLength == 0) break;
                    uint64_t segNum = (uint64_t)entry->SegmentReference.SegmentNumberLowPart |
                        ((uint64_t)entry->SegmentReference.SegmentNumberHighPart << 32);
                    if (segNum != 0) {
                        bool found = false;
                        for (auto r : extensionRecords) if (r == segNum) { found = true; break; }
                        if (!found) extensionRecords.push_back(segNum);
                    }
                    offset += entry->RecordLength;
                }

                uint8_t extRecord[FILE_RECORD_SIZE];
                for (auto recNum : extensionRecords) {
                    if (!ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, recNum, extRecord))
                        continue;
                    auto* extHdr = (PFILE_RECORD_SEGMENT_HEADER)extRecord;
                    if (extHdr->MultiSectorHeader.Magic != 0x454C4946) continue;

                    auto* extAttr = (PATTRIBUTE_RECORD_HEADER)(extRecord + extHdr->FirstAttributeOffset);
                    while (extAttr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
                        if (extAttr->RecordLength == 0) break;
                        if (extAttr->TypeCode == Data) {
                            auto additionalRuns = ParseDataRuns(extAttr);
                            for (auto& r : additionalRuns) mftRuns.push_back(r);
                        }
                        extAttr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)extAttr + extAttr->RecordLength);
                    }
                }
                free(attrListData);
            }
        }

        uint64_t totalMftBytes = 0;
        for (auto& run : mftRuns) totalMftBytes += run.clusterCount * bytesPerCluster;
        uint64_t totalRecords = totalMftBytes / FILE_RECORD_SIZE;

        VolumeReadContext ctx;
        ctx.volumeHandle = volumeHandle;
        ctx.mftRuns = &mftRuns;
        ctx.bytesPerCluster = bytesPerCluster;
        ctx.runIndex = 0;
        ctx.filesRemaining = mftRuns[0].clusterCount * bytesPerCluster / FILE_RECORD_SIZE;
        ctx.positionInBlock = 0;
        ctx.bufferSizeRecords = bufferSizeRecords;

        free(result);
        return ParseMFTImpl(VolumeReadChunk, &ctx, totalRecords, filter, matchFlags, bufferSizeRecords);
    }

    EXPORT MftParseResult* ParseMFTFromFile(const wchar_t* filePath, const wchar_t* filter, uint32_t matchFlags, uint32_t bufferSizeRecords) {
        HANDLE hFile = CreateFileW(filePath, GENERIC_READ, FILE_SHARE_READ, nullptr,
                                   OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) {
            auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
            if (result) SetErrorMessage(result->errorMessage, L"Failed to open file. Error: %lu", GetLastError());
            return result;
        }

        LARGE_INTEGER fileSize;
        GetFileSizeEx(hFile, &fileSize);
        uint64_t totalRecords = fileSize.QuadPart / FILE_RECORD_SIZE;

        FileReadContext ctx = { hFile, totalRecords, bufferSizeRecords };
        auto* result = ParseMFTImpl(FileReadChunk, &ctx, totalRecords, filter, matchFlags, bufferSizeRecords);
        CloseHandle(hFile);
        return result;
    }
}
