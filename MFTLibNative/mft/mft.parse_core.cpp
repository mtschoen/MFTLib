// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.parse_core.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdlib>
#include <cstring>
#include <mutex>
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
    CompactOutput output{};
    double ioMs = 0.0;
    double fixupMs = 0.0;
    double parseMs = 0.0;
};

// One chunk's global record base plus its record count.
struct ChunkSpan {
    uint64_t recordIndex;
    uint64_t chunkSize;
};

struct ChunkReader {
    ReadChunkFn readChunk = nullptr;
    void* readContext = nullptr;
    std::array<uint8_t*, 2>* buf = nullptr;
};

// Allocate the two double-buffer I/O buffers and the initial compact entry/string arrays.
// On failure, frees whatever was allocated (plus lookup when resolvePaths), sets the
// error message, and returns false. (big_free is a no-op on nullptr.)
bool AllocateParseBuffers(std::array<uint8_t*, 2>& buf, size_t bufSize, PathLookup& lookup, bool resolvePaths,
                          ParseState& state, MftParseResult* result) {
    buf[0] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    buf[1] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    if ((buf[0] == nullptr) || (buf[1] == nullptr)) {
        mftlib::platform::big_free(buf[0], bufSize);
        mftlib::platform::big_free(buf[1], bufSize);
        if (resolvePaths) {
            lookup.cleanup();
        }
        SetErrorMessage(result->errorMessage, L"Failed to allocate I/O buffers");
        return false;
    }

    state.output.entries = ShouldFailAlloc()
                               ? nullptr
                               : static_cast<MftCompactEntry*>(
                                     malloc(static_cast<size_t>(state.output.entryCapacity) * sizeof(MftCompactEntry)));
    if (state.output.entries == nullptr) {
        mftlib::platform::big_free(buf[0], bufSize);
        mftlib::platform::big_free(buf[1], bufSize);
        if (resolvePaths) {
            lookup.cleanup();
        }
        SetErrorMessage(result->errorMessage, L"Failed to allocate entry array");
        return false;
    }

    state.output.strings =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<uint16_t*>(malloc(static_cast<size_t>(state.output.stringCapacity) * sizeof(uint16_t)));
    if (state.output.strings == nullptr) {
        free(state.output.entries);
        state.output.entries = nullptr;
        mftlib::platform::big_free(buf[0], bufSize);
        mftlib::platform::big_free(buf[1], bufSize);
        if (resolvePaths) {
            lookup.cleanup();
        }
        SetErrorMessage(result->errorMessage, L"Failed to allocate string pool");
        return false;
    }
    return true;
}

// Apply USA fixups to every valid record in buffer[range.start, range.end).
void FixupRange(uint8_t* buffer, SliceRange range, ParseGeometry geometry) {
    for (uint64_t i = range.start; i < range.end; i++) {
        auto* recPtr = buffer + (static_cast<size_t>(geometry.recordSize) * i);
        const auto* rec = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(recPtr);
        if (rec->MultiSectorHeader.Magic == 0x454C4946) {
            ApplyFixup(recPtr, geometry.recordSize);
        }
    }
}

// Fix up and parse one chunk across numThreads workers, then merge their slices.
// Returns false if the merge ran out of memory (error already set).
bool ParseChunkParallel(uint8_t* buffer, ChunkSpan chunk, unsigned numThreads, const ScanContext& scan,
                        ParseState& state, MftParseResult* result) {
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
        slices[ti].entries.reserve(initCap);
        slices[ti].strings.reserve(initCap * 32);
        uint64_t recordIndex = chunk.recordIndex;
        workers.emplace_back([buffer, tStart, tEnd, ti, &slices, &threadFixupMs, scan, recordIndex]() {
            auto fStart = SteadyClock::now();
            FixupRange(buffer, SliceRange{tStart, tEnd}, scan.geometry);
            threadFixupMs[ti] = ElapsedMs(fStart, SteadyClock::now());
            ProcessRecordSlice(buffer, SliceRange{tStart, tEnd}, recordIndex, &slices[ti], scan);
        });
    }
    for (auto& worker : workers) {
        worker.join();
    }

    double totalElapsed = ElapsedMs(fixupStart, SteadyClock::now());
    double maxFixup =
        (actualThreads > 0) ? *std::max_element(threadFixupMs.begin(), threadFixupMs.begin() + actualThreads) : 0.0;
    state.fixupMs += maxFixup;
    state.parseMs += totalElapsed - maxFixup;

    for (unsigned ti = 0; ti < actualThreads; ti++) {
        if (!AppendSlice(state.output, slices[ti], result->errorMessage)) {
            return false;
        }
    }
    return true;
}

struct ProgressHook {
    MftProgressCallback callback = nullptr;
    void* context = nullptr;
    SteadyClock::time_point wallStart;
};

struct ResolveProgressState {
    MftProgressCallback callback = nullptr;
    void* context = nullptr;
    SteadyClock::time_point wallStart;
    uint64_t totalEntries = 0;
    uint64_t entriesResolved = 0;
    std::mutex callbackMutex;

    void reportBatch(uint64_t batchCount) {
        if (callback == nullptr) {
            return;
        }
        std::lock_guard<std::mutex> lock(callbackMutex);
        entriesResolved += batchCount;
        uint64_t resolved = entriesResolved;
        if (resolved > totalEntries) {
            resolved = totalEntries;
        }
        if (resolved < totalEntries) {
            double elapsedMs = ElapsedMs(wallStart, SteadyClock::now());
            callback(MftScanPhase::ResolvingPaths, resolved, totalEntries, elapsedMs, context);
        }
    }

    void reportFinal() {
        if (callback == nullptr) {
            return;
        }
        std::lock_guard<std::mutex> lock(callbackMutex);
        entriesResolved = totalEntries;
        double elapsedMs = ElapsedMs(wallStart, SteadyClock::now());
        callback(MftScanPhase::ResolvingPaths, totalEntries, totalEntries, elapsedMs, context);
    }
};

void PopulatePathSlice(SliceRange range, const CompactOutput& source, const PathLookup& lookup, uint64_t totalRecords,
                       SliceResult& slice, ResolveProgressState* progressState) {
    std::vector<uint16_t> pathBuffer;
    slice.entries.reserve(range.end - range.start);
    constexpr uint64_t kReportInterval = 4096;
    uint64_t localCount = 0;

    for (uint64_t i = range.start; i < range.end; i++) {
        const auto& src = source.entries[i];
        ParsedEntry entry{};
        entry.recordNumber = src.recordNumber;
        entry.parentRecordNumber = src.parentRecordNumber;
        entry.flags = src.flags;
        entry.fileAttributes = src.fileAttributes;
        entry.size = src.size;
        entry.modifiedTime = src.modifiedTime;
        if (ResolvePath(src.recordNumber, lookup, totalRecords, pathBuffer)) {
            entry.name = reinterpret_cast<const WCHAR*>(pathBuffer.data());
            entry.nameLength = static_cast<uint16_t>(pathBuffer.size());
        } else {
            entry.name = nullptr;
            entry.nameLength = 0;
        }
        slice.append(entry);

        if (progressState != nullptr && ++localCount >= kReportInterval) {
            progressState->reportBatch(localCount);
            localCount = 0;
        }
    }

    if (progressState != nullptr && localCount > 0) {
        progressState->reportBatch(localCount);
    }
}

// Resolve a full path for every parsed entry, fanning out across numThreads workers.
void ResolveAllPaths(uint64_t totalRecords, const PathLookup& lookup, unsigned numThreads, ParseState& state,
                     MftParseResult* result, const ProgressHook& progress) {
    uint64_t usedCount = state.output.entryCount;
    if (usedCount == 0) {
        return;
    }

    CompactOutput paths;
    paths.entryCapacity = usedCount;
    paths.entries =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<MftCompactEntry*>(malloc(static_cast<size_t>(usedCount) * sizeof(MftCompactEntry)));
    if (paths.entries == nullptr) {
        return;
    }

    paths.stringCapacity = std::max<uint64_t>(state.output.stringUnits, 1024);
    paths.strings = ShouldFailAlloc()
                        ? nullptr
                        : static_cast<uint16_t*>(malloc(static_cast<size_t>(paths.stringCapacity) * sizeof(uint16_t)));
    if (paths.strings == nullptr) {
        free(paths.entries);
        return;
    }

    ResolveProgressState progressState;
    progressState.callback = progress.callback;
    progressState.context = progress.context;
    progressState.wallStart = progress.wallStart;
    progressState.totalEntries = usedCount;

    std::vector<SliceResult> pathSlices(numThreads);
    if (numThreads > 1) {
        uint64_t perThread = (usedCount + numThreads - 1) / numThreads;
        std::vector<std::thread> workers;
        for (unsigned ti = 0; ti < numThreads; ti++) {
            uint64_t start = (std::min)(static_cast<uint64_t>(ti) * perThread, usedCount);
            uint64_t end = (std::min)(start + perThread, usedCount);
            workers.emplace_back(PopulatePathSlice, SliceRange{start, end}, std::cref(state.output), std::cref(lookup),
                                 totalRecords, std::ref(pathSlices[ti]), &progressState);
        }
        for (auto& worker : workers) {
            worker.join();
        }
    } else {
        PopulatePathSlice(SliceRange{0, usedCount}, state.output, lookup, totalRecords, pathSlices[0], &progressState);
    }

    std::array<wchar_t, 256> dummyError{};
    bool appendOk = true;
    for (unsigned ti = 0; ti < numThreads; ti++) {
        if (!AppendSlice(paths, pathSlices[ti], dummyError.data())) {
            appendOk = false;
            break;
        }
    }

    if (!appendOk) {
        free(paths.entries);
        free(paths.strings);
        return;
    }

    // Atomic publication on success
    result->pathEntries = paths.entries;
    result->pathStrings = paths.strings;
    result->pathStringUnits = paths.stringUnits;
    free(state.output.entries);
    free(state.output.strings);
    state.output.entries = nullptr;
    state.output.strings = nullptr;
    progressState.reportFinal();
    state.output.entryCount = 0;
    state.output.stringUnits = 0;
    state.output.entryCapacity = 0;
    state.output.stringCapacity = 0;
}

// Drive the double-buffered read/parse loop over every chunk. Returns false if a
// chunk's merge ran out of memory (result error already set; buffers freed by caller).
bool ParseAllChunks(ChunkReader& reader, unsigned numThreads, const ScanContext& scan, ParseState& state,
                    MftParseResult* result, const ProgressHook& progress) {
    uint64_t recordIndex = 0;
    uint64_t lastReportedRecords = 0;
    uint64_t currentChunkSize = reader.readChunk(reader.readContext, (*reader.buf)[0], state.ioMs);
    int curBuf = 0;

    while (currentChunkSize > 0) {
        uint64_t nextChunkSize = 0;
        double nextIoMs = 0;
        std::thread ioThread(
            [&]() { nextChunkSize = reader.readChunk(reader.readContext, (*reader.buf)[1 - curBuf], nextIoMs); });

        uint8_t* buffer = (*reader.buf)[curBuf];

        if (numThreads > 1) {
            if (!ParseChunkParallel(buffer, ChunkSpan{recordIndex, currentChunkSize}, numThreads, scan, state,
                                    result)) {
                ioThread.join();
                return false;
            }
        } else {
            auto fixupStart = SteadyClock::now();
            FixupRange(buffer, SliceRange{0, currentChunkSize}, scan.geometry);
            state.fixupMs += ElapsedMs(fixupStart, SteadyClock::now());

            SliceResult batchSlice;
            batchSlice.entries.reserve((scan.filter.text != nullptr) ? 64 : currentChunkSize / 4);
            batchSlice.strings.reserve(batchSlice.entries.capacity() * 32);

            auto parseStart = SteadyClock::now();
            ProcessRecordBatch(buffer, currentChunkSize, recordIndex, batchSlice, scan);
            state.parseMs += ElapsedMs(parseStart, SteadyClock::now());

            if (!AppendSlice(state.output, batchSlice, result->errorMessage)) {
                ioThread.join();
                return false;
            }
        }

        recordIndex += currentChunkSize;

        if (progress.callback != nullptr) {
            double elapsedMs = ElapsedMs(progress.wallStart, SteadyClock::now());
            progress.callback(MftScanPhase::Parsing, recordIndex, scan.totalRecords, elapsedMs, progress.context);
            lastReportedRecords = recordIndex;
        }

        ioThread.join();
        state.ioMs += nextIoMs;

        currentChunkSize = nextChunkSize;
        curBuf = 1 - curBuf;
    }

    if (progress.callback != nullptr && lastReportedRecords < scan.totalRecords) {
        double elapsedMs = ElapsedMs(progress.wallStart, SteadyClock::now());
        progress.callback(MftScanPhase::Parsing, scan.totalRecords, scan.totalRecords, elapsedMs, progress.context);
    }

    return true;
}

}  // namespace

MftParseResult* ParseMFTImpl(ReadChunkFn readChunk, void* readContext, uint64_t totalRecords, FilterSpec filter,
                             uint32_t bufferSizeRecords, ParseGeometry geometry, MftProgressCallback callback,
                             void* progressContext) {
    auto wallStart = SteadyClock::now();

    auto* result = ShouldFailAlloc() ? nullptr : static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
    if (result == nullptr) {
        return nullptr;
    }
    result->totalRecords = totalRecords;
    result->abiVersion = MFT_NATIVE_ABI_VERSION;
    result->entryStride = sizeof(MftCompactEntry);

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
    const size_t bufSize = static_cast<size_t>(bufferSizeRecords) * geometry.recordSize;

    ParseState state = {};
    state.output.entryCapacity = std::max<uint64_t>((filter.text != nullptr) ? 1024 : totalRecords / 4, 1024);
    state.output.stringCapacity = std::max<uint64_t>(state.output.entryCapacity * 32, 1024);

    std::array<uint8_t*, 2> buf = {};
    if (!AllocateParseBuffers(buf, bufSize, lookup, resolvePaths, state, result)) {
        return result;
    }

    ScanContext scan{filter, resolvePaths ? &lookup : nullptr, totalRecords, geometry};
    ChunkReader reader{readChunk, readContext, &buf};
    ProgressHook progress{callback, progressContext, wallStart};
    bool parsedOk = ParseAllChunks(reader, numThreads, scan, state, result, progress);

    mftlib::platform::big_free(buf[0], bufSize);
    mftlib::platform::big_free(buf[1], bufSize);

    if (!parsedOk) {
        if (resolvePaths) {
            lookup.cleanup();
        }
        result->entries = state.output.entries;
        result->usedRecords = state.output.entryCount;
        result->entryStrings = state.output.strings;
        result->entryStringUnits = state.output.stringUnits;
        return result;
    }

    if (resolvePaths && lookup.namesDropped.load(std::memory_order_relaxed) > 0) {
        SetErrorMessage(result->errorMessage, L"Path name pool exhausted; %llu names dropped, some paths truncated",
                        static_cast<unsigned long long>(lookup.namesDropped.load(std::memory_order_relaxed)));
    }

    uint64_t parsedCount = state.output.entryCount;
    if (resolvePaths && parsedCount > 0) {
        ResolveAllPaths(totalRecords, lookup, numThreads, state, result, progress);
        lookup.cleanup();
    }

    result->usedRecords = parsedCount;
    if (result->pathEntries == nullptr) {
        result->entries = state.output.entries;
        result->entryStrings = state.output.strings;
        result->entryStringUnits = state.output.stringUnits;
    }

    result->ioTimeMs = state.ioMs;
    result->fixupTimeMs = state.fixupMs;
    result->parseTimeMs = state.parseMs;
    result->totalTimeMs = ElapsedMs(wallStart, SteadyClock::now());
    return result;
}
