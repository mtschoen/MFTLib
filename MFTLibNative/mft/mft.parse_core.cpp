// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.parse_core.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <algorithm>
#include <array>
#include <cstdlib>
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
    uint64_t invalidFixupRecords = 0;
};

// One chunk's global record base plus its record count.
struct ChunkSpan {
    uint64_t recordIndex;
    uint64_t chunkSize;
};

struct ChunkReader {
    const ParseSource* source = nullptr;
    std::array<uint8_t*, 2>* buf = nullptr;

    uint64_t read(int bufferIndex, double& ioMs) const {
        return source->readChunk(source->context, (*buf)[bufferIndex], ioMs);
    }

    bool incomplete() const { return source->incomplete != nullptr && source->incomplete(source->context); }
};

// Allocate the two double-buffer I/O buffers and the initial compact entry/string arrays.
// On failure, frees whatever was allocated, sets the error message, and returns false.
// (big_free is a no-op on nullptr.)
bool AllocateParseBuffers(std::array<uint8_t*, 2>& buf, size_t bufSize, ParseState& state, MftParseResult* result) {
    buf[0] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    buf[1] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    if ((buf[0] == nullptr) || (buf[1] == nullptr)) {
        mftlib::platform::big_free(buf[0], bufSize);
        mftlib::platform::big_free(buf[1], bufSize);
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
        SetErrorMessage(result->errorMessage, L"Failed to allocate string pool");
        return false;
    }
    return true;
}

// A parse checks for cancellation between sub-slices of this many records.
constexpr uint64_t kCancelCheckRecords = 4096;

enum class ParseOutcome : uint8_t { Completed, Failed, Cancelled, Incomplete };

bool IsCancelRequested(const MftParseControl* control) {
    return ShouldForceCancel() || (control != nullptr && LoadSharedInt32(&control->cancelRequested) != 0);
}

// Apply USA fixups to every FILE record in buffer[range.start, range.end). A record whose fixup
// is invalid is never decoded: its signature is cleared, so the record scan passes over it.
// Returns how many of those records were allocated.
uint64_t FixupRange(uint8_t* buffer, SliceRange range, ParseGeometry geometry) {
    uint64_t allocatedInvalidRecords = 0;
    for (uint64_t i = range.start; i < range.end; i++) {
        auto* recPtr = buffer + (static_cast<size_t>(geometry.recordSize) * i);
        auto* rec = reinterpret_cast<FILE_RECORD_SEGMENT_HEADER*>(recPtr);
        if (rec->MultiSectorHeader.Magic != kFileRecordMagic || ApplyFixup(recPtr, geometry.recordSize)) {
            continue;
        }
        allocatedInvalidRecords += (rec->Flags & kRecordInUse) != 0 ? 1 : 0;
        rec->MultiSectorHeader.Magic = 0;
    }
    return allocatedInvalidRecords;
}

// Fix up and parse buffer[range) in kCancelCheckRecords-record sub-slices, stopping between
// sub-slices once cancellation is requested, or at once when the scan rejects invalid fixups
// and an allocated record has one. Returns the milliseconds the fixups took.
double FixupAndParseSlice(uint8_t* buffer, SliceRange range, uint64_t recordBase, SliceResult& slice,
                          const ScanContext& scan) {
    double fixupMs = 0.0;
    for (uint64_t start = range.start; start < range.end && !IsCancelRequested(scan.control);
         start += kCancelCheckRecords) {
        const SliceRange subSlice{start, (std::min)(start + kCancelCheckRecords, range.end)};
        auto fixupStart = SteadyClock::now();
        slice.invalidFixupRecords += FixupRange(buffer, subSlice, scan.geometry);
        fixupMs += ElapsedMs(fixupStart, SteadyClock::now());
        if (slice.invalidFixupRecords != 0 && scan.rejectInvalidFixup) {
            break;
        }
        ProcessRecordSlice(buffer, subSlice, recordBase, &slice, scan);
    }
    return fixupMs;
}

// Fail the parse because an allocated record of untrusted input has an invalid fixup.
bool RejectInvalidFixup(MftParseResult* result) {
    SetErrorMessage(result->errorMessage, L"The dump contains an invalid MFT record fixup.");
    result->invalidInput = 1;
    return false;
}

// Fix up and parse one chunk on the calling thread, then merge it.
// Returns false if a fixup was rejected or the merge ran out of memory (error already set).
bool ParseChunkSerial(uint8_t* buffer, ChunkSpan chunk, const ScanContext& scan, ParseState& state,
                      MftParseResult* result) {
    SliceResult batchSlice;
    batchSlice.entries.reserve(chunk.chunkSize / 4);
    batchSlice.strings.reserve(batchSlice.entries.capacity() * 32);

    auto parseStart = SteadyClock::now();
    double fixupMs = FixupAndParseSlice(buffer, SliceRange{0, chunk.chunkSize}, chunk.recordIndex, batchSlice, scan);
    state.fixupMs += fixupMs;
    state.parseMs += ElapsedMs(parseStart, SteadyClock::now()) - fixupMs;

    if (batchSlice.invalidFixupRecords != 0 && scan.rejectInvalidFixup) {
        return RejectInvalidFixup(result);
    }
    state.invalidFixupRecords += batchSlice.invalidFixupRecords;
    return AppendSlice(state.output, batchSlice, result->errorMessage);
}

// Fix up and parse one chunk across numThreads workers, then merge their slices.
// Returns false if a fixup was rejected or the merge ran out of memory (error already set).
bool ParseChunkParallel(uint8_t* buffer, ChunkSpan chunk, unsigned numThreads, const ScanContext& scan,
                        ParseState& state, MftParseResult* result) {
    auto fixupStart = SteadyClock::now();
    std::vector<SliceResult> slices(numThreads);
    std::vector<double> threadFixupMs(numThreads, 0.0);
    unsigned actualThreads = ForEachRange(chunk.chunkSize, numThreads, [&](unsigned index, SliceRange range) {
        const uint64_t initialCapacity = std::max<uint64_t>((range.end - range.start) / 4, 64);
        slices[index].entries.reserve(initialCapacity);
        slices[index].strings.reserve(initialCapacity * 32);
        threadFixupMs[index] = FixupAndParseSlice(buffer, range, chunk.recordIndex, slices[index], scan);
    });

    double totalElapsed = ElapsedMs(fixupStart, SteadyClock::now());
    double maxFixup =
        (actualThreads > 0) ? *std::max_element(threadFixupMs.begin(), threadFixupMs.begin() + actualThreads) : 0.0;
    state.fixupMs += maxFixup;
    state.parseMs += totalElapsed - maxFixup;

    uint64_t invalidFixupRecords = 0;
    for (const auto& slice : slices) {
        invalidFixupRecords += slice.invalidFixupRecords;
    }
    if (invalidFixupRecords != 0 && scan.rejectInvalidFixup) {
        return RejectInvalidFixup(result);
    }
    state.invalidFixupRecords += invalidFixupRecords;
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

// Drive the double-buffered read/parse loop over every chunk. Each chunk reads the thread
// allowance once at its start and keeps that count until it is merged. Failed means a chunk
// rejected a fixup or its merge ran out of memory (result error already set); Cancelled means
// cancellation was seen before a chunk read or after a chunk's parse; Incomplete means the
// source reported, after its last chunk, that the input ended early, failed or changed size.
// Buffers are freed by the caller either way.
ParseOutcome ParseAllChunks(ChunkReader& reader, const ScanContext& scan, ParseState& state, MftParseResult* result,
                            const ProgressHook& progress) {
    if (IsCancelRequested(scan.control)) {
        return ParseOutcome::Cancelled;
    }
    uint64_t recordIndex = 0;
    uint64_t lastReportedRecords = 0;
    uint64_t currentChunkSize = reader.read(0, state.ioMs);
    int curBuf = 0;

    while (currentChunkSize > 0) {
        if (IsCancelRequested(scan.control)) {
            return ParseOutcome::Cancelled;
        }
        const unsigned numThreads = EffectiveThreadCount(scan.control);
        RecordChunkThreadCount(numThreads);

        uint64_t nextChunkSize = 0;
        double nextIoMs = 0;
        std::thread ioThread([&]() { nextChunkSize = reader.read(1 - curBuf, nextIoMs); });

        uint8_t* buffer = (*reader.buf)[curBuf];
        const ChunkSpan chunk{recordIndex, currentChunkSize};
        const bool mergedOk = numThreads > 1 ? ParseChunkParallel(buffer, chunk, numThreads, scan, state, result)
                                             : ParseChunkSerial(buffer, chunk, scan, state, result);
        if (!mergedOk) {
            ioThread.join();
            return ParseOutcome::Failed;
        }

        recordIndex += currentChunkSize;

        if (IsCancelRequested(scan.control)) {
            ioThread.join();
            return ParseOutcome::Cancelled;
        }

        if (progress.callback != nullptr) {
            double elapsedMs = ElapsedMs(progress.wallStart, SteadyClock::now());
            progress.callback(recordIndex, scan.totalRecords, elapsedMs, progress.context);
            lastReportedRecords = recordIndex;
        }

        ioThread.join();
        state.ioMs += nextIoMs;

        currentChunkSize = nextChunkSize;
        curBuf = 1 - curBuf;
    }

    if (reader.incomplete()) {
        return ParseOutcome::Incomplete;
    }

    if (progress.callback != nullptr && lastReportedRecords < scan.totalRecords) {
        double elapsedMs = ElapsedMs(progress.wallStart, SteadyClock::now());
        progress.callback(scan.totalRecords, scan.totalRecords, elapsedMs, progress.context);
    }

    return ParseOutcome::Completed;
}

// Free the partial output of a parse that stops without a usable result.
void DiscardOutput(ParseState& state) {
    free(state.output.entries);
    free(state.output.strings);
    state.output = CompactOutput{};
}

// Free the partial output of a cancelled parse and mark its result cancelled.
MftParseResult* FinishCancelled(ParseState& state, MftParseResult* result) {
    DiscardOutput(state);
    result->cancelled = 1;
    SetErrorMessage(result->errorMessage, L"Parse cancelled");
    return result;
}

// Free the partial output of a parse whose input ended early, failed or changed size.
MftParseResult* FinishIncomplete(ParseState& state, MftParseResult* result) {
    DiscardOutput(state);
    result->invalidInput = 1;
    SetErrorMessage(result->errorMessage, L"The dump file could not be read completely.");
    return result;
}

}  // namespace

MftParseResult* CreateParseResult(const wchar_t* message) {
    auto* result = ShouldFailAlloc() ? nullptr : static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
    if (result == nullptr) {
        return nullptr;
    }
    result->abiVersion = MFT_NATIVE_ABI_VERSION;
    result->entryStride = sizeof(MftCompactEntry);
    if (message != nullptr) {
        SetErrorMessage(result->errorMessage, message);
    }
    return result;
}

MftParseResult* ParseMFTImpl(const ParseSource& source, const ParseRequest& request) {
    auto wallStart = SteadyClock::now();
    ResetRecordedParseThreadCounts();

    auto* result = CreateParseResult();
    if (result == nullptr) {
        return nullptr;
    }
    const uint64_t totalRecords = source.totalRecords;
    const ParseGeometry geometry = source.geometry;
    const MftParseControl* control = request.control;
    const uint32_t bufferSizeRecords = request.bufferSizeRecords;
    result->totalRecords = totalRecords;

    const size_t bufSize = static_cast<size_t>(bufferSizeRecords) * geometry.recordSize;

    ParseState state = {};
    // The record total comes from an unvalidated length, so it only sizes the first allocation
    // up to a fixed ceiling; a larger result grows as records are actually parsed.
    constexpr uint64_t kMaximumInitialEntries = uint64_t{4} * 1024 * 1024;
    state.output.entryCapacity = std::clamp<uint64_t>(totalRecords / 4, 1024, kMaximumInitialEntries);
    state.output.stringCapacity = std::max<uint64_t>(state.output.entryCapacity * 32, 1024);

    std::array<uint8_t*, 2> buf = {};
    if (!AllocateParseBuffers(buf, bufSize, state, result)) {
        return result;
    }

    const ScanContext scan{request.includeFreed, totalRecords, geometry, control, source.rejectInvalidFixup};
    ChunkReader reader{&source, &buf};
    ProgressHook progress{request.callback, request.progressContext, wallStart};
    const ParseOutcome outcome = ParseAllChunks(reader, scan, state, result, progress);

    mftlib::platform::big_free(buf[0], bufSize);
    mftlib::platform::big_free(buf[1], bufSize);

    if (outcome == ParseOutcome::Cancelled) {
        return FinishCancelled(state, result);
    }
    if (outcome == ParseOutcome::Incomplete) {
        return FinishIncomplete(state, result);
    }
    if (outcome == ParseOutcome::Failed) {
        result->entries = state.output.entries;
        result->usedRecords = state.output.entryCount;
        result->entryStrings = state.output.strings;
        result->entryStringUnits = state.output.stringUnits;
        return result;
    }

    result->usedRecords = state.output.entryCount;
    result->invalidFixupRecords = state.invalidFixupRecords;
    result->entries = state.output.entries;
    result->entryStrings = state.output.strings;
    result->entryStringUnits = state.output.stringUnits;

    result->ioTimeMs = state.ioMs;
    result->fixupTimeMs = state.fixupMs;
    result->parseTimeMs = state.parseMs;
    result->totalTimeMs = ElapsedMs(wallStart, SteadyClock::now());
    return result;
}
