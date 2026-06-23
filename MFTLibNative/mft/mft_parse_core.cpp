#include "pch.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/ntfs_io.h"
#include "../core/platform.h"
#include "mft_internal.h"

using namespace mftlib::ntfs;

MftParseResult* ParseMFTImpl(ReadChunkFn readChunk, void* readContext, uint64_t totalRecords, const wchar_t* filter,
                             uint32_t matchFlags, uint32_t bufferSizeRecords) {
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

            {
                auto fixupStart = SteadyClock::now();
                std::vector<SliceResult> slices(numThreads);
                std::vector<std::thread> workers;
                std::vector<double> threadFixupMs(numThreads, 0.0);
                unsigned actualThreads = 0;

                for (unsigned ti = 0; ti < numThreads; ti++) {
                    uint64_t tStart = ti * perThread;
                    uint64_t tEnd = tStart + perThread < currentChunkSize ? tStart + perThread : currentChunkSize;
                    if (tStart >= currentChunkSize) {
                        break;
                    }
                    actualThreads++;

                    uint64_t initCap = (filter != nullptr) ? 64 : (tEnd - tStart) / 4;
                    initCap = std::max<uint64_t>(initCap, 64);
                    slices[ti].init(initCap);

                    workers.emplace_back([buffer, tStart, tEnd, ti, &slices, &threadFixupMs, filter, filterLen,
                                          matchFlags, &lookup, resolvePaths, totalRecords, recordIndex]() {
                        auto fStart = SteadyClock::now();
                        for (uint64_t i = tStart; i < tEnd; i++) {
                            auto* recPtr = buffer + (FILE_RECORD_SIZE * i);
                            auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);
                            if (rec->MultiSectorHeader.Magic == 0x454C4946) {
                                ApplyFixup(recPtr, FILE_RECORD_SIZE);
                            }
                        }
                        threadFixupMs[ti] = ElapsedMs(fStart, SteadyClock::now());

                        ProcessRecordSlice(buffer, tStart, tEnd, recordIndex, &slices[ti], filter, filterLen,
                                           matchFlags, resolvePaths ? &lookup : nullptr, totalRecords);
                    });
                }
                for (auto& worker : workers) {
                    worker.join();
                }

                auto combinedEnd = SteadyClock::now();
                double totalElapsed = ElapsedMs(fixupStart, combinedEnd);

                double maxFixup = 0;
                for (unsigned ti = 0; ti < actualThreads; ti++) {
                    maxFixup = (std::max)(threadFixupMs[ti], maxFixup);
                }
                fixupMs += maxFixup;
                parseMs += totalElapsed - maxFixup;

                for (unsigned ti = 0; ti < actualThreads; ti++) {
                    auto& slice = slices[ti];
                    if (slice.count > 0) {
                        if (usedCount + slice.count > capacity) {
                            while (usedCount + slice.count > capacity) {
                                capacity *= 2;
                            }
                            result->entries = static_cast<MftFileEntry*>(
                                realloc(result->entries, static_cast<size_t>(capacity) * sizeof(MftFileEntry)));
                        }
                        memcpy(result->entries + usedCount, slice.entries,
                               static_cast<size_t>(slice.count) * sizeof(MftFileEntry));
                        usedCount += slice.count;
                    }
                    slice.cleanup();
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
                for (unsigned ti = 0; ti < numThreads; ti++) {
                    uint64_t start = (std::min)(static_cast<uint64_t>(ti) * perThread, usedCount);
                    uint64_t end = (std::min)(start + perThread, usedCount);
                    workers.emplace_back(resolveRange, start, end);
                }
                for (auto& worker : workers) {
                    worker.join();
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
