#include "pch.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/ntfs_io.h"
#include "../core/platform.h"

static bool FileNameMatches(const WCHAR* name, uint8_t nameLen, const wchar_t* filter, uint16_t filterLen,
                            uint32_t matchFlags) {
#ifdef _WIN32
    if ((matchFlags & 1) != 0U) {
        if (nameLen != filterLen) {
            return false;
        }
        return _wcsnicmp(name, filter, nameLen) == 0;
    }
    if ((matchFlags & 2) != 0U) {
        if (filterLen > nameLen) {
            return false;
        }
        for (uint16_t i = 0; i <= nameLen - filterLen; i++) {
            if (_wcsnicmp(name + i, filter, filterLen) == 0) {
                return true;
            }
        }
        return false;
    }
#else
    (void)name;
    (void)nameLen;
    (void)filter;
    (void)filterLen;
    (void)matchFlags;
#endif
    return false;
}

struct PathLookup {
    uint64_t* parents;
    uint8_t* nameLens;
    uint32_t* nameOffsets;
    // namePool stores raw NTFS UTF-16 bytes (2 bytes per WCHAR unit).
    // On Windows wchar_t==WCHAR so this is a direct match.
    // On Linux wchar_t is 32-bit, so we use a byte pool and keep sizes in code units.
    uint8_t* namePool;
    std::atomic<uint64_t> namePoolUsed;
    uint64_t namePoolCapacity;
    // Count of names dropped because the pool filled up. Nonzero means some
    // resolved paths are truncated; surfaced to the caller via errorMessage.
    std::atomic<uint64_t> namesDropped;

    bool init(uint64_t totalRecords) {
        parents = static_cast<uint64_t*>(calloc(static_cast<size_t>(totalRecords), sizeof(uint64_t)));
        nameLens = static_cast<uint8_t*>(calloc(static_cast<size_t>(totalRecords), sizeof(uint8_t)));
        nameOffsets = static_cast<uint32_t*>(calloc(static_cast<size_t>(totalRecords), sizeof(uint32_t)));
        // Each name entry can be up to 255 WCHAR units = 510 bytes; use 32 bytes avg * 2 for bytes.
        // A test hook can shrink the pool to exercise the exhaustion path.
        uint64_t capacityOverride = NamePoolCapacityOverride();
        namePoolCapacity = (capacityOverride != 0U) ? capacityOverride : totalRecords * 64;  // bytes
        namePool = static_cast<uint8_t*>(malloc(static_cast<size_t>(namePoolCapacity)));
        namePoolUsed = 0;
        namesDropped = 0;
        return (parents != nullptr) && (nameLens != nullptr) && (nameOffsets != nullptr) && (namePool != nullptr);
    }

    // Store a name from an NTFS FileName attribute (WCHAR* = char16_t*, nameLen in WCHAR units)
    void storeName(uint64_t recordIndex, uint64_t parent, const WCHAR* name, uint8_t nameLen) {
        parents[recordIndex] = parent;
        uint64_t byteCount = static_cast<uint64_t>(nameLen) * sizeof(WCHAR);
        uint64_t offset = namePoolUsed.fetch_add(byteCount, std::memory_order_relaxed);
        if (offset + byteCount > namePoolCapacity) {
            namesDropped.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        nameOffsets[recordIndex] = static_cast<uint32_t>(offset);
        memcpy(namePool + offset, name, static_cast<size_t>(byteCount));
        nameLens[recordIndex] = nameLen;
    }

    void cleanup() const {
        free(parents);
        free(nameLens);
        free(nameOffsets);
        free(namePool);
    }
};

// Copy NTFS WCHAR units (UTF-16, char16_t) to wchar_t, combining surrogate pairs on Linux.
// Returns the number of wchar_t codepoints written.
static uint16_t CopyNtfsNameUnits(wchar_t* dst, uint16_t dstCapacity, const WCHAR* src, uint16_t srcLen) {
    uint16_t out = 0;
    for (uint16_t i = 0; i < srcLen && out < dstCapacity; i++) {
        uint16_t unit;
        memcpy(&unit, src + i, sizeof(WCHAR));
#ifndef _WIN32
        if (unit >= 0xD800 && unit <= 0xDBFF && i + 1 < srcLen) {
            uint16_t low;
            memcpy(&low, src + i + 1, sizeof(WCHAR));
            if (low >= 0xDC00 && low <= 0xDFFF) {
                uint32_t codepoint = 0x10000u + (((uint32_t)(unit - 0xD800)) << 10) + (low - 0xDC00);
                dst[out++] = (wchar_t)codepoint;
                i++;
                continue;
            }
        }
#endif
        dst[out++] = static_cast<wchar_t>(unit);
    }
    return out;
}

// Copy a NTFS WCHAR name (UTF-16, char16_t units) into a wchar_t buffer.
// On Windows (wchar_t==uint16_t) this is a direct unit copy.
// On Linux (wchar_t==uint32_t) surrogate pairs are combined into a single codepoint.
// Always null-terminates if there's room (callers pass dstCapacity > srcLen in practice).
static void CopyNtfsName(wchar_t* dst, uint16_t dstCapacity, const WCHAR* src, uint16_t srcLen) {
    uint16_t out = CopyNtfsNameUnits(dst, dstCapacity, src, srcLen);
    if (out < dstCapacity) {
        dst[out] = L'\0';
    }
}

static uint16_t ResolvePath(uint64_t recordIndex, PathLookup& lookup, uint64_t totalRecords, wchar_t* pathBuf,
                            uint16_t pathBufSize) {
    struct Component {
        const uint8_t* nameBytes;
        uint8_t len;
    };
    Component stack[128] = {};
    int depth = 0;
    uint64_t current = recordIndex;
    uint64_t visited[128] = {};
    int visitCount = 0;

    while (current != 5 && current < totalRecords && depth < 128) {
        bool cycle = false;
        for (int v = 0; v < visitCount; v++) {
            if (visited[v] == current) {
                cycle = true;
                break;
            }
        }
        if (cycle) {
            break;
        }
        visited[visitCount++] = current;

        if (lookup.nameLens[current] == 0) {
            break;
        }
        stack[depth].nameBytes = lookup.namePool + lookup.nameOffsets[current];
        stack[depth].len = lookup.nameLens[current];
        depth++;
        current = lookup.parents[current];
    }

    uint16_t pos = 0;
    for (int i = depth - 1; i >= 0; i--) {
        if (pos + stack[i].len + 1 >= pathBufSize) {
            break;
        }
        if (pos > 0) {
            pathBuf[pos++] = L'\\';
        }
        const uint8_t* src = stack[i].nameBytes;
        uint8_t len = stack[i].len;
        // Copy with surrogate-pair combining; Clamp to remaining buffer space.
        auto remaining = static_cast<uint16_t>(pathBufSize - pos);
        uint16_t written = CopyNtfsNameUnits(pathBuf + pos, remaining, reinterpret_cast<const WCHAR*>(src),
                                             static_cast<uint16_t>(len));
        pos += written;
    }
    pathBuf[pos] = L'\0';
    return pos;
}

struct SliceResult {
    MftFileEntry* entries;
    uint64_t count;
    uint64_t capacity;

    void init(uint64_t cap) {
        entries = static_cast<MftFileEntry*>(malloc(static_cast<size_t>(cap) * sizeof(MftFileEntry)));
        count = 0;
        capacity = cap;
    }
    void cleanup() const { free(entries); }
};

static void ProcessRecordSlice(uint8_t* buffer, uint64_t startIdx, uint64_t endIdx, uint64_t recordBase,
                               SliceResult* slice, const wchar_t* filter, uint16_t filterLen, uint32_t matchFlags,
                               PathLookup* lookup, uint64_t totalRecords) {
    for (uint64_t i = startIdx; i < endIdx; i++) {
        uint64_t recordIndex = recordBase + i;
        auto* recPtr = buffer + (FILE_RECORD_SIZE * i);
        auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);

        if (rec->MultiSectorHeader.Magic != 0x454C4946) {
            continue;
        }
        if ((rec->Flags & 0x0001) == 0) {
            continue;
        }

        uint64_t baseRef = static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberLowPart) |
                           (static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberHighPart) << 32);
        if (baseRef != 0) {
            continue;
        }

        auto* attr =
            reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(rec) + rec->FirstAttributeOffset);
        uint32_t siAttributes = 0;
        bool sawStandardInformation = false;
        while (reinterpret_cast<uint8_t*>(attr) - reinterpret_cast<uint8_t*>(rec) < FILE_RECORD_SIZE) {
            if (attr->TypeCode == EndMarker || attr->RecordLength == 0) {
                break;
            }

            if (attr->TypeCode == StandardInformation && attr->FormCode == 0) {
                auto* siValue = reinterpret_cast<uint8_t*>(attr) + attr->Form.Resident.ValueOffset;
                siAttributes = *reinterpret_cast<uint32_t*>(siValue + 32);
                sawStandardInformation = true;
            } else if (attr->TypeCode == FileName && attr->FormCode == 0) {
                auto* fn =
                    reinterpret_cast<PFILE_NAME>(reinterpret_cast<uint8_t*>(attr) + attr->Form.Resident.ValueOffset);
                if (fn->Flags != 2) {
                    uint64_t parent = static_cast<uint64_t>(fn->ParentDirectory.SegmentNumberLowPart) |
                                      (static_cast<uint64_t>(fn->ParentDirectory.SegmentNumberHighPart) << 32);

                    if ((lookup != nullptr) && recordIndex < totalRecords) {
                        lookup->storeName(recordIndex, parent, fn->FileName, fn->FileNameLength);
                    }

                    if ((filter != nullptr) &&
                        !FileNameMatches(fn->FileName, fn->FileNameLength, filter, filterLen, matchFlags)) {
                        break;
                    }

                    if (slice->count >= slice->capacity) {
                        slice->capacity *= 2;
                        slice->entries = static_cast<MftFileEntry*>(
                            realloc(slice->entries, static_cast<size_t>(slice->capacity) * sizeof(MftFileEntry)));
                    }

                    auto& entry = slice->entries[slice->count];
                    memset(&entry, 0, sizeof(MftFileEntry));
                    entry.recordNumber = recordIndex;
                    entry.parentRecordNumber = parent;
                    entry.flags = rec->Flags;
                    entry.fileNameLength = fn->FileNameLength;
                    entry.fileAttributes = sawStandardInformation ? siAttributes : fn->FileAttributes;
                    // FileNameLength is UCHAR (max 255) which fits in fileName[260]; no clamping needed.
                    CopyNtfsName(entry.fileName, 260, fn->FileName, fn->FileNameLength);

                    slice->count++;
                    break;
                }
            }
            attr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(attr) + attr->RecordLength);
        }
    }
}

static void ProcessRecordBatch(uint8_t* buffer, uint64_t filesToLoad, uint64_t& recordIndex, MftParseResult* result,
                               uint64_t& usedCount, uint64_t& capacity, const wchar_t* filter, uint16_t filterLen,
                               uint32_t matchFlags, PathLookup* lookup, uint64_t totalRecords) {
    for (uint64_t i = 0; i < filesToLoad; i++, recordIndex++) {
        auto* recPtr = buffer + (FILE_RECORD_SIZE * i);
        auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);

        if (rec->MultiSectorHeader.Magic != 0x454C4946) {
            continue;
        }
        if ((rec->Flags & 0x0001) == 0) {
            continue;
        }

        uint64_t baseRef = static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberLowPart) |
                           (static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberHighPart) << 32);
        if (baseRef != 0) {
            continue;
        }

        auto* attr =
            reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(rec) + rec->FirstAttributeOffset);
        uint32_t siAttributes = 0;
        bool sawStandardInformation = false;
        while (reinterpret_cast<uint8_t*>(attr) - reinterpret_cast<uint8_t*>(rec) < FILE_RECORD_SIZE) {
            if (attr->TypeCode == EndMarker || attr->RecordLength == 0) {
                break;
            }

            if (attr->TypeCode == StandardInformation && attr->FormCode == 0) {
                auto* siValue = reinterpret_cast<uint8_t*>(attr) + attr->Form.Resident.ValueOffset;
                siAttributes = *reinterpret_cast<uint32_t*>(siValue + 32);
                sawStandardInformation = true;
            } else if (attr->TypeCode == FileName && attr->FormCode == 0) {
                auto* fn =
                    reinterpret_cast<PFILE_NAME>(reinterpret_cast<uint8_t*>(attr) + attr->Form.Resident.ValueOffset);
                if (fn->Flags != 2) {
                    uint64_t parent = static_cast<uint64_t>(fn->ParentDirectory.SegmentNumberLowPart) |
                                      (static_cast<uint64_t>(fn->ParentDirectory.SegmentNumberHighPart) << 32);

                    if ((lookup != nullptr) && recordIndex < totalRecords) {
                        lookup->storeName(recordIndex, parent, fn->FileName, fn->FileNameLength);
                    }

                    if ((filter != nullptr) &&
                        !FileNameMatches(fn->FileName, fn->FileNameLength, filter, filterLen, matchFlags)) {
                        break;
                    }

                    if (usedCount >= capacity) {
                        uint64_t newCapacity = capacity * 2;
                        auto* grown =
                            ShouldFailAlloc()
                                ? nullptr
                                : static_cast<MftFileEntry*>(realloc(
                                      result->entries, static_cast<size_t>(newCapacity) * sizeof(MftFileEntry)));
                        if (grown == nullptr) {
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
                    // FileNameLength is UCHAR (max 255) which fits in fileName[260]; no clamping needed.
                    CopyNtfsName(entry.fileName, 260, fn->FileName, fn->FileNameLength);

                    usedCount++;
                    break;
                }
            }
            attr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(attr) + attr->RecordLength);
        }
    }
}

using ReadChunkFn = uint64_t (*)(void* context, uint8_t* targetBuffer, double& ioMs);

static MftParseResult* ParseMFTImpl(ReadChunkFn readChunk, void* readContext, uint64_t totalRecords,
                                    const wchar_t* filter, uint32_t matchFlags, uint32_t bufferSizeRecords) {
    auto wallStart = SteadyClock::now();
    double ioMs = 0;
    double fixupMs = 0;
    double parseMs = 0;

    auto* result = ShouldFailAlloc() ? nullptr : static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
    if (result == nullptr) {
        return nullptr;
    }

    result->totalRecords = totalRecords;

    uint16_t filterLen = (filter != nullptr) ? static_cast<uint16_t>(wcslen(filter)) : 0;
    bool resolvePaths = (matchFlags & 4) != 0;

    PathLookup lookup = {};
    if (resolvePaths) {
        if (ShouldFailAlloc() || !lookup.init(totalRecords)) {
            SetErrorMessage(result->errorMessage, L"Failed to allocate path lookup");
            return result;
        }
    }

    unsigned numThreads = EffectiveThreadCount();

    const size_t bufSize = static_cast<size_t>(bufferSizeRecords) * FILE_RECORD_SIZE;
    uint8_t* buf[2] = {};
    buf[0] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    buf[1] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    if ((buf[0] == nullptr) || (buf[1] == nullptr)) {
        if (buf[0] != nullptr) {
            mftlib::platform::big_free(buf[0], bufSize);
        }
        if (buf[1] != nullptr) {
            mftlib::platform::big_free(buf[1], bufSize);
        }
        if (resolvePaths) {
            lookup.cleanup();
        }
        SetErrorMessage(result->errorMessage, L"Failed to allocate I/O buffers");
        return result;
    }

    uint64_t capacity = (filter != nullptr) ? 1024 : totalRecords / 4;
    capacity = std::max<uint64_t>(capacity, 1024);
    result->entries = ShouldFailAlloc()
                          ? nullptr
                          : static_cast<MftFileEntry*>(malloc(static_cast<size_t>(capacity) * sizeof(MftFileEntry)));
    if (result->entries == nullptr) {
        mftlib::platform::big_free(buf[0], bufSize);
        mftlib::platform::big_free(buf[1], bufSize);
        if (resolvePaths) {
            lookup.cleanup();
        }
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
        std::thread ioThread([&]() { nextChunkSize = readChunk(readContext, buf[1 - curBuf], nextIoMs); });

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
                    uint64_t tEnd = tStart + perThread < currentChunkSize ? tStart + perThread : currentChunkSize;
                    if (tStart >= currentChunkSize) {
                        break;
                    }
                    actualThreads++;

                    uint64_t initCap = (filter != nullptr) ? 64 : (tEnd - tStart) / 4;
                    initCap = std::max<uint64_t>(initCap, 64);
                    slices[t].init(initCap);

                    workers.emplace_back([buffer, tStart, tEnd, t, &slices, &threadFixupMs, filter, filterLen,
                                          matchFlags, &lookup, resolvePaths, totalRecords, recordIndex]() {
                        auto fStart = SteadyClock::now();
                        for (uint64_t i = tStart; i < tEnd; i++) {
                            auto* recPtr = buffer + (FILE_RECORD_SIZE * i);
                            auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);
                            if (rec->MultiSectorHeader.Magic == 0x454C4946) {
                                ApplyFixup(recPtr, FILE_RECORD_SIZE);
                            }
                        }
                        threadFixupMs[t] = ElapsedMs(fStart, SteadyClock::now());

                        ProcessRecordSlice(buffer, tStart, tEnd, recordIndex, &slices[t], filter, filterLen, matchFlags,
                                           resolvePaths ? &lookup : nullptr, totalRecords);
                    });
                }
                for (auto& w : workers) {
                    w.join();
                }

                auto combinedEnd = SteadyClock::now();
                double totalElapsed = ElapsedMs(fixupStart, combinedEnd);

                double maxFixup = 0;
                for (unsigned t = 0; t < actualThreads; t++) {
                    maxFixup = (std::max)(threadFixupMs[t], maxFixup);
                }
                fixupMs += maxFixup;
                parseMs += totalElapsed - maxFixup;

                for (unsigned t = 0; t < actualThreads; t++) {
                    auto& s = slices[t];
                    if (s.count > 0) {
                        if (usedCount + s.count > capacity) {
                            while (usedCount + s.count > capacity) {
                                capacity *= 2;
                            }
                            result->entries = static_cast<MftFileEntry*>(
                                realloc(result->entries, static_cast<size_t>(capacity) * sizeof(MftFileEntry)));
                        }
                        memcpy(result->entries + usedCount, s.entries,
                               static_cast<size_t>(s.count) * sizeof(MftFileEntry));
                        usedCount += s.count;
                    }
                    s.cleanup();
                }
            }
        } else {
            auto fixupStart = SteadyClock::now();
            for (uint64_t i = 0; i < currentChunkSize; i++) {
                auto* recPtr = buffer + (FILE_RECORD_SIZE * i);
                auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);
                if (rec->MultiSectorHeader.Magic == 0x454C4946) {
                    ApplyFixup(recPtr, FILE_RECORD_SIZE);
                }
            }
            fixupMs += ElapsedMs(fixupStart, SteadyClock::now());

            auto parseStart = SteadyClock::now();
            ProcessRecordBatch(buffer, currentChunkSize, recordIndex, result, usedCount, capacity, filter, filterLen,
                               matchFlags, resolvePaths ? &lookup : nullptr, totalRecords);
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

    mftlib::platform::big_free(buf[0], bufSize);
    mftlib::platform::big_free(buf[1], bufSize);

    if (resolvePaths && lookup.namesDropped.load(std::memory_order_relaxed) > 0) {
        SetErrorMessage(result->errorMessage, L"Path name pool exhausted; %llu names dropped, some paths truncated",
                        static_cast<unsigned long long>(lookup.namesDropped.load(std::memory_order_relaxed)));
    }

    if (resolvePaths && usedCount > 0) {
        result->pathEntries = static_cast<MftPathEntry*>(calloc(static_cast<size_t>(usedCount), sizeof(MftPathEntry)));
        if (result->pathEntries != nullptr) {
            auto resolveRange = [&](uint64_t start, uint64_t end) {
                for (uint64_t i = start; i < end; i++) {
                    auto& src = result->entries[i];
                    auto& dst = result->pathEntries[i];
                    dst.recordNumber = src.recordNumber;
                    dst.parentRecordNumber = src.parentRecordNumber;
                    dst.flags = src.flags;
                    dst.fileAttributes = src.fileAttributes;
                    dst.pathLength = ResolvePath(src.recordNumber, lookup, totalRecords, dst.path, 1024);
                }
            };

            // Path resolution is read-only on the lookup and writes to independent
            // output slots, so it fans out the same way fixup+parse does. Ranges are
            // clamped with std::min, so extra workers simply get empty ranges.
            if (numThreads > 1) {
                uint64_t perThread = (usedCount + numThreads - 1) / numThreads;
                std::vector<std::thread> workers;
                for (unsigned t = 0; t < numThreads; t++) {
                    uint64_t start = (std::min)(static_cast<uint64_t>(t) * perThread, usedCount);
                    uint64_t end = (std::min)(start + perThread, usedCount);
                    workers.emplace_back(resolveRange, start, end);
                }
                for (auto& w : workers) {
                    w.join();
                }
            } else {
                resolveRange(0, usedCount);
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

#ifdef _WIN32
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
    auto* c = static_cast<VolumeReadContext*>(ctx);
    while (c->filesRemaining == 0) {
        c->runIndex++;
        if (c->runIndex >= c->mftRuns->size()) {
            return 0;
        }
        auto& run = (*c->mftRuns)[c->runIndex];
        c->filesRemaining = run.clusterCount * c->bytesPerCluster / FILE_RECORD_SIZE;
        c->positionInBlock = 0;
    }

    auto& run = (*c->mftRuns)[c->runIndex];
    uint64_t filesToLoad = c->filesRemaining < static_cast<uint64_t>(c->bufferSizeRecords)
                               ? c->filesRemaining
                               : static_cast<uint64_t>(c->bufferSizeRecords);
    DWORD readBytes;
    auto ioStart = SteadyClock::now();
    if (Read(c->volumeHandle, targetBuffer,
             (static_cast<uint64_t>(run.clusterOffset) * c->bytesPerCluster) + c->positionInBlock,
             static_cast<DWORD>(filesToLoad * FILE_RECORD_SIZE), &readBytes) == 0) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    c->positionInBlock += filesToLoad * FILE_RECORD_SIZE;
    c->filesRemaining -= filesToLoad;
    return filesToLoad;
}
#endif  // _WIN32

struct FileReadContext {
    mftlib::platform::File* file;
    uint64_t recordsRemaining;
    uint32_t bufferSizeRecords;
    int64_t fileOffset;  // current read position in bytes
};

static uint64_t FileReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* c = static_cast<FileReadContext*>(ctx);
    if (c->recordsRemaining == 0) {
        return 0;
    }
    uint64_t filesToLoad = c->recordsRemaining < static_cast<uint64_t>(c->bufferSizeRecords)
                               ? c->recordsRemaining
                               : static_cast<uint64_t>(c->bufferSizeRecords);
    auto byteCount = static_cast<size_t>(filesToLoad * FILE_RECORD_SIZE);
    auto ioStart = SteadyClock::now();
    if (ShouldFailRead()) {
        return 0;
    }
    int64_t bytesRead = mftlib::platform::pread_at(c->file, targetBuffer, byteCount, c->fileOffset);
    if (bytesRead <= 0) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    uint64_t recordsRead = static_cast<uint64_t>(bytesRead) / FILE_RECORD_SIZE;
    c->fileOffset += static_cast<int64_t>(recordsRead) * FILE_RECORD_SIZE;
    c->recordsRemaining -= recordsRead;
    return recordsRead;
}

// Core implementation that takes a UTF-8 file path.
static MftParseResult* ParseMFTFromFileImpl(const char* path_utf8, const wchar_t* filter, uint32_t matchFlags,
                                            uint32_t bufferSizeRecords) {
#ifndef _WIN32
    if (filter != nullptr) {
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (result) SetErrorMessage(result->errorMessage, L"Filter not supported on Linux yet");
        return result;
    }
#endif

    auto* file = mftlib::platform::open_read(path_utf8);
    if (file == nullptr) {
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
        if (result != nullptr) {
            SetErrorMessage(result->errorMessage, L"Failed to open file. Error: %lu",
                            static_cast<unsigned long>(mftlib::platform::last_error()));
        }
        return result;
    }

    int64_t fileSize = ShouldFailFileSize() ? -1 : mftlib::platform::size_of(file);
    if (fileSize < 0) {
        mftlib::platform::close_file(file);
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
        if (result != nullptr) {
            SetErrorMessage(result->errorMessage, L"Failed to get file size");
        }
        return result;
    }

    uint64_t totalRecords = static_cast<uint64_t>(fileSize) / FILE_RECORD_SIZE;
    FileReadContext ctx = {file, totalRecords, bufferSizeRecords, 0};
    auto* result = ParseMFTImpl(FileReadChunk, &ctx, totalRecords, filter, matchFlags, bufferSizeRecords);
    mftlib::platform::close_file(file);
    return result;
}

extern "C" {
EXPORT void FreeMftResult(MftParseResult* result) {
    if (result != nullptr) {
        free(result->entries);
        free(result->pathEntries);
        free(result);
    }
}

#ifdef _WIN32
EXPORT MftParseResult* ParseMFTRecords(HANDLE volumeHandle, const wchar_t* filter, uint32_t matchFlags,
                                       uint32_t bufferSizeRecords) {
    auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
    if (result == nullptr) {
        return nullptr;
    }

    if (volumeHandle == INVALID_HANDLE_VALUE) {
        SetErrorMessage(result->errorMessage, L"Volume handle is invalid");
        return result;
    }

    NTFS_BPB bpb;
    DWORD bytesRead;
    if ((Read(volumeHandle, &bpb, 0, 512, &bytesRead) == 0) || bytesRead != 512) {
        SetErrorMessage(result->errorMessage, L"Failed to read boot sector. Error: %lu", GetLastError());
        return result;
    }
    if (bpb.name[0] != 'N' || bpb.name[1] != 'T' || bpb.name[2] != 'F' || bpb.name[3] != 'S') {
        SetErrorMessage(result->errorMessage, L"Volume is not NTFS");
        return result;
    }

    uint32_t bytesPerCluster = bpb.bytesPerSector * bpb.sectorsPerCluster;

    uint8_t record0[FILE_RECORD_SIZE];
    if ((Read(volumeHandle, record0, bpb.mftStart * bytesPerCluster, FILE_RECORD_SIZE, &bytesRead) == 0) ||
        bytesRead != FILE_RECORD_SIZE) {
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
    if (dataAttr == nullptr) {
        SetErrorMessage(result->errorMessage, L"No Data attribute in MFT record 0");
        return result;
    }
    auto mftRuns = ParseDataRuns(dataAttr);

    auto* attrListAttr = FindAttribute(record0, AttributeList);
    if (attrListAttr != nullptr) {
        uint8_t* attrListData;
        uint64_t attrListSize = 0;

        if (attrListAttr->FormCode == 1) {
            attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
        } else {
            attrListSize = attrListAttr->Form.Resident.ValueLength;
            attrListData = (uint8_t*)malloc((size_t)attrListSize);
            if (attrListData != nullptr) {
                memcpy(attrListData, (uint8_t*)attrListAttr + attrListAttr->Form.Resident.ValueOffset,
                       (size_t)attrListSize);
            }
        }

        if (attrListData != nullptr) {
            std::vector<uint64_t> extensionRecords;
            uint64_t offset = 0;
            while (offset + sizeof(ATTRIBUTE_LIST_ENTRY) <= attrListSize) {
                auto* entry = (PATTRIBUTE_LIST_ENTRY)(attrListData + offset);
                if (entry->RecordLength == 0) {
                    break;
                }
                uint64_t segNum = (uint64_t)entry->SegmentReference.SegmentNumberLowPart |
                                  ((uint64_t)entry->SegmentReference.SegmentNumberHighPart << 32);
                if (segNum != 0) {
                    bool found = false;
                    for (auto r : extensionRecords) {
                        if (r == segNum) {
                            found = true;
                            break;
                        }
                    }
                    if (!found) {
                        extensionRecords.push_back(segNum);
                    }
                }
                offset += entry->RecordLength;
            }

            uint8_t extRecord[FILE_RECORD_SIZE];
            for (auto recNum : extensionRecords) {
                if (!ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, recNum, extRecord)) {
                    continue;
                }
                auto* extHdr = (PFILE_RECORD_SEGMENT_HEADER)extRecord;
                if (extHdr->MultiSectorHeader.Magic != 0x454C4946) {
                    continue;
                }

                auto* extAttr = (PATTRIBUTE_RECORD_HEADER)(extRecord + extHdr->FirstAttributeOffset);
                while (extAttr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
                    if (extAttr->RecordLength == 0) {
                        break;
                    }
                    if (extAttr->TypeCode == Data) {
                        auto additionalRuns = ParseDataRuns(extAttr);
                        for (auto& r : additionalRuns) {
                            mftRuns.push_back(r);
                        }
                    }
                    extAttr = (PATTRIBUTE_RECORD_HEADER)((uint8_t*)extAttr + extAttr->RecordLength);
                }
            }
            free(attrListData);
        }
    }

    uint64_t totalMftBytes = 0;
    for (auto& run : mftRuns) {
        totalMftBytes += run.clusterCount * bytesPerCluster;
    }
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

EXPORT MftParseResult* ParseMFTFromFile(const wchar_t* filePath, const wchar_t* filter, uint32_t matchFlags,
                                        uint32_t bufferSizeRecords) {
    int u8len =
        ShouldFailPathConversion() ? 0 : WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    if (u8len <= 0) {
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (result != nullptr) {
            SetErrorMessage(result->errorMessage, L"Failed to convert path to UTF-8");
        }
        return result;
    }
    std::string utf8(static_cast<size_t>(u8len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8.data(), u8len, nullptr, nullptr);
    return ParseMFTFromFileImpl(utf8.c_str(), filter, matchFlags, bufferSizeRecords);
}
#endif  // _WIN32

#ifndef _WIN32
EXPORT MftParseResult* ParseMFTFromFileUtf8(const char* filePath, const wchar_t* filter, uint32_t matchFlags,
                                            uint32_t bufferSizeRecords) {
    return ParseMFTFromFileImpl(filePath, filter, matchFlags, bufferSizeRecords);
}
#endif
}
