// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.parse_core.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <algorithm>
#include <array>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/platform.h"
#include "mft.internal.h"

using namespace mftlib::ntfs;
using namespace mftlib::ntfs::detail;

namespace {

// Mutable state threaded through the parse pipeline, so the helpers take one
// reference instead of a long list of same-typed in/out counters and timers.
struct ParseState {
    EntryBuffer entries;  // result, usedCount, capacity
    double ioMs;
    double fixupMs;
    double parseMs;
};

// One chunk's global record base plus its record count.
struct ChunkSpan {
    uint64_t recordIndex;
    uint64_t chunkSize;
};

// Allocate the two double-buffer I/O buffers and the initial entry array. On
// failure, frees whatever was allocated (plus lookup when resolvePaths), sets the
// error message, and returns false. (big_free is a no-op on nullptr.)
bool AllocateParseBuffers(std::array<uint8_t*, 2>& buf, size_t bufSize, PathLookup& lookup, bool resolvePaths,
                          ParseState& state) {
    buf[0] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    buf[1] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    if ((buf[0] == nullptr) || (buf[1] == nullptr)) {
        mftlib::platform::big_free(buf[0], bufSize);
        mftlib::platform::big_free(buf[1], bufSize);
        if (resolvePaths) {
            lookup.cleanup();
        }
        SetErrorMessage(state.entries.result->errorMessage, L"Failed to allocate I/O buffers");
        return false;
    }

    state.entries.result->entries =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<MftFileEntry*>(malloc(static_cast<size_t>(state.entries.capacity) * sizeof(MftFileEntry)));
    if (state.entries.result->entries == nullptr) {
        mftlib::platform::big_free(buf[0], bufSize);
        mftlib::platform::big_free(buf[1], bufSize);
        if (resolvePaths) {
            lookup.cleanup();
        }
        SetErrorMessage(state.entries.result->errorMessage, L"Failed to allocate entry array");
        return false;
    }
    return true;
}

// Apply USA fixups to every valid record in buffer[range.start, range.end).
void FixupRange(uint8_t* buffer, SliceRange range) {
    for (uint64_t i = range.start; i < range.end; i++) {
        auto* recPtr = buffer + (FILE_RECORD_SIZE * i);
        auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);
        if (rec->MultiSectorHeader.Magic == 0x454C4946) {
            ApplyFixup(recPtr, FILE_RECORD_SIZE);
        }
    }
}

// Copy each worker slice's entries into result->entries, growing it as needed.
// Returns false (after setting the error message) if a growth realloc fails.
bool MergeSlices(std::vector<SliceResult>& slices, unsigned actualThreads, ParseState& state) {
    for (unsigned ti = 0; ti < actualThreads; ti++) {
        auto& slice = slices[ti];
        uint64_t sliceCount = slice.entries.size();
        if (sliceCount == 0) {
            continue;
        }
        if (state.entries.usedCount + sliceCount > state.entries.capacity) {
            uint64_t newCapacity = state.entries.capacity;
            while (state.entries.usedCount + sliceCount > newCapacity) {
                newCapacity *= 2;
            }
            auto* grown =
                ShouldFailAlloc()
                    ? nullptr
                    : static_cast<MftFileEntry*>(realloc(state.entries.result->entries,
                                                         static_cast<size_t>(newCapacity) * sizeof(MftFileEntry)));
            if (grown == nullptr) {
                SetErrorMessage(state.entries.result->errorMessage, L"Failed to grow entry array");
                state.entries.result->usedRecords = state.entries.usedCount;
                return false;
            }
            state.entries.result->entries = grown;
            state.entries.capacity = newCapacity;
        }
        memcpy(state.entries.result->entries + state.entries.usedCount, slice.entries.data(),
               static_cast<size_t>(sliceCount) * sizeof(MftFileEntry));
        state.entries.usedCount += sliceCount;
    }
    return true;
}

// Fix up and parse one chunk across numThreads workers, then merge their slices.
// Returns false if the merge ran out of memory (error already set).
bool ParseChunkParallel(uint8_t* buffer, ChunkSpan chunk, unsigned numThreads, const ScanContext& scan,
                        ParseState& state) {
    auto fixupStart = SteadyClock::now();
    uint64_t perThread = (chunk.chunkSize + numThreads - 1) / numThreads;
    std::vector<SliceResult> slices(numThreads);
    std::vector<std::thread> workers;
    std::vector<double> threadFixupMs(numThreads, 0.0);
    unsigned actualThreads = 0;

    for (unsigned ti = 0; ti < numThreads; ti++) {
        uint64_t tStart = ti * perThread;
        uint64_t tEnd = tStart + perThread < chunk.chunkSize ? tStart + perThread : chunk.chunkSize;
        if (tStart >= chunk.chunkSize) {
            break;
        }
        actualThreads++;
        uint64_t initCap = (scan.filter.text != nullptr) ? 64 : (tEnd - tStart) / 4;
        initCap = std::max<uint64_t>(initCap, 64);
        slices[ti].init(initCap);
        uint64_t recordIndex = chunk.recordIndex;
        workers.emplace_back([buffer, tStart, tEnd, ti, &slices, &threadFixupMs, scan, recordIndex]() {
            auto fStart = SteadyClock::now();
            FixupRange(buffer, SliceRange{tStart, tEnd});
            threadFixupMs[ti] = ElapsedMs(fStart, SteadyClock::now());
            ProcessRecordSlice(buffer, SliceRange{tStart, tEnd}, recordIndex, &slices[ti], scan);
        });
    }
    for (auto& worker : workers) {
        worker.join();
    }

    double totalElapsed = ElapsedMs(fixupStart, SteadyClock::now());
    double maxFixup = 0;
    for (unsigned ti = 0; ti < actualThreads; ti++) {
        maxFixup = (std::max)(threadFixupMs[ti], maxFixup);
    }
    state.fixupMs += maxFixup;
    state.parseMs += totalElapsed - maxFixup;

    return MergeSlices(slices, actualThreads, state);
}

// Resolve a full path for every parsed entry, fanning out across numThreads workers.
void ResolveAllPaths(uint64_t totalRecords, PathLookup& lookup, unsigned numThreads, ParseState& state) {
    MftParseResult* result = state.entries.result;
    uint64_t usedCount = state.entries.usedCount;
    result->pathEntries =
        ShouldFailAlloc() ? nullptr
                          : static_cast<MftPathEntry*>(calloc(static_cast<size_t>(usedCount), sizeof(MftPathEntry)));
    if (result->pathEntries == nullptr) {
        return;
    }
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

    // Path resolution is read-only on the lookup and writes to independent output
    // slots, so it fans out the same way fixup+parse does. Ranges are clamped with
    // std::min, so extra workers simply get empty ranges.
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

// Drive the double-buffered read/parse loop over every chunk. Returns false if a
// chunk's merge ran out of memory (result error already set; buffers freed by caller).
bool ParseAllChunks(ReadChunkFn readChunk, void* readContext, std::array<uint8_t*, 2>& buf, unsigned numThreads,
                    const ScanContext& scan, ParseState& state) {
    uint64_t recordIndex = 0;
    uint64_t currentChunkSize = readChunk(readContext, buf[0], state.ioMs);
    int curBuf = 0;

    while (currentChunkSize > 0) {
        uint64_t nextChunkSize = 0;
        double nextIoMs = 0;
        std::thread ioThread([&]() { nextChunkSize = readChunk(readContext, buf[1 - curBuf], nextIoMs); });

        uint8_t* buffer = buf[curBuf];

        if (numThreads > 1) {
            if (!ParseChunkParallel(buffer, ChunkSpan{recordIndex, currentChunkSize}, numThreads, scan, state)) {
                ioThread.join();
                return false;
            }
        } else {
            auto fixupStart = SteadyClock::now();
            FixupRange(buffer, SliceRange{0, currentChunkSize});
            state.fixupMs += ElapsedMs(fixupStart, SteadyClock::now());

            auto parseStart = SteadyClock::now();
            ProcessRecordBatch(buffer, currentChunkSize, recordIndex, state.entries, scan);
            state.parseMs += ElapsedMs(parseStart, SteadyClock::now());
            if (state.entries.result->errorMessage[0] != L'\0') {
                ioThread.join();
                break;
            }
        }

        recordIndex += currentChunkSize;

        ioThread.join();
        state.ioMs += nextIoMs;

        currentChunkSize = nextChunkSize;
        curBuf = 1 - curBuf;
    }
    return true;
}

}  // namespace

MftParseResult* ParseMFTImpl(ReadChunkFn readChunk, void* readContext, uint64_t totalRecords, FilterSpec filter,
                             uint32_t bufferSizeRecords) {
    auto wallStart = SteadyClock::now();

    auto* result = ShouldFailAlloc() ? nullptr : static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
    if (result == nullptr) {
        return nullptr;
    }
    result->totalRecords = totalRecords;

    filter.length = (filter.text != nullptr) ? static_cast<uint16_t>(wcslen(filter.text)) : 0;
    bool resolvePaths = (filter.flags & 4) != 0;

    PathLookup lookup = {};
    if (resolvePaths) {
        if (ShouldFailAlloc() || !lookup.init(totalRecords)) {
            SetErrorMessage(result->errorMessage, L"Failed to allocate path lookup");
            return result;
        }
    }

    unsigned numThreads = EffectiveThreadCount();
    const size_t bufSize = static_cast<size_t>(bufferSizeRecords) * FILE_RECORD_SIZE;

    ParseState state = {};
    state.entries.result = result;
    state.entries.capacity = std::max<uint64_t>((filter.text != nullptr) ? 1024 : totalRecords / 4, 1024);

    std::array<uint8_t*, 2> buf = {};
    if (!AllocateParseBuffers(buf, bufSize, lookup, resolvePaths, state)) {
        return result;
    }

    ScanContext scan{filter, resolvePaths ? &lookup : nullptr, totalRecords};
    bool parsedOk = ParseAllChunks(readChunk, readContext, buf, numThreads, scan, state);

    mftlib::platform::big_free(buf[0], bufSize);
    mftlib::platform::big_free(buf[1], bufSize);

    if (!parsedOk) {
        if (resolvePaths) {
            lookup.cleanup();
        }
        return result;
    }

    if (resolvePaths && lookup.namesDropped.load(std::memory_order_relaxed) > 0) {
        SetErrorMessage(result->errorMessage, L"Path name pool exhausted; %llu names dropped, some paths truncated",
                        static_cast<unsigned long long>(lookup.namesDropped.load(std::memory_order_relaxed)));
    }

    if (resolvePaths && state.entries.usedCount > 0) {
        ResolveAllPaths(totalRecords, lookup, numThreads, state);
        lookup.cleanup();
    }

    result->usedRecords = state.entries.usedCount;
    result->ioTimeMs = state.ioMs;
    result->fixupTimeMs = state.fixupMs;
    result->parseTimeMs = state.parseMs;
    result->totalTimeMs = ElapsedMs(wallStart, SteadyClock::now());
    return result;
}
