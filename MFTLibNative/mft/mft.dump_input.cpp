// The file side of the MFT parser: a dump (a saved $MFT image) is opened once, inspected, and
// parsed through that one open file. A dump is untrusted input, so nothing read from it is
// believed before it is checked, and a file that fails a check is rejected, never repaired.
#include "pch.h"

#include <algorithm>
#include <array>
// ReSharper disable once CppUnusedIncludeDirective
// aislop-ignore-next-line CppUnusedIncludeDirective -- memcpy and memcmp below need this on GCC/Clang
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/platform.h"
#include "mft.internal.h"

// One opened dump file with the length and record size inspection found. A parse reads this
// file and no other, so replacing the path after the open cannot change what is parsed.
// Declared opaque in mft_api.h because the exports below hand it to the caller.
struct MftDumpInput {
    mftlib::platform::File* file = nullptr;
    uint64_t lengthBytes = 0;
    ParseGeometry geometry{};
};

namespace {

const auto* const kIncompleteReadMessage = L"The dump file could not be read completely.";

// What inspecting an opened file found. rejection is null when the file is accepted;
// invalidInput says the rejection is about the file's content and not about reaching it.
struct DumpInspection {
    const wchar_t* rejection = nullptr;
    bool invalidInput = false;
    uint64_t lengthBytes = 0;
    ParseGeometry geometry{};
};

DumpInspection RejectContent(const wchar_t* message) { return DumpInspection{message, true}; }

// Sizes the file and reads its geometry from record zero: the FILE signature and the
// allocated record size at byte 0x1C, which must be a size the parser supports and must
// divide the file length exactly.
DumpInspection InspectDump(const mftlib::platform::File* file) {
    const int64_t fileSize = ShouldFailFileSize() ? -1 : mftlib::platform::size_of(file);
    if (fileSize < 0) {
        return DumpInspection{L"Failed to get file size", false};
    }
    if (fileSize == 0) {
        return RejectContent(L"The dump file is empty.");
    }

    std::array<uint8_t, 0x20> header{};
    const int64_t headerBytesRead =
        mftlib::platform::pread_at(file, header.data(), header.size(), mftlib::platform::FileOffset{});
    if (headerBytesRead < static_cast<int64_t>(header.size())) {
        return RejectContent(kIncompleteReadMessage);
    }
    uint32_t recordSize = 0;
    memcpy(&recordSize, header.data() + 0x1C, sizeof(recordSize));
    if (memcmp(header.data(), "FILE", 4) != 0 || !IsSupportedRecordSize(recordSize)) {
        return RejectContent(L"Invalid or unsupported MFT record size.");
    }
    if (static_cast<uint64_t>(fileSize) % recordSize != 0) {
        return RejectContent(L"File size is not a whole multiple of record size.");
    }
    return DumpInspection{nullptr, false, static_cast<uint64_t>(fileSize), ParseGeometry{recordSize}};
}

struct DumpReadContext {
    const MftDumpInput* input = nullptr;
    uint64_t recordsRemaining = 0;
    uint32_t bufferSizeRecords = 0;
    int64_t fileOffset = 0;
    bool readFailed = false;
    // Extra handles on the same file, so the parts of one chunk read concurrently: a synchronous
    // handle serializes the reads issued through it.
    std::vector<mftlib::platform::File*> partFiles;
};

// Reads exactly byteCount bytes at offset, in calls small enough for every platform read.
// False when a read fails or the file ends first.
bool ReadExactly(const mftlib::platform::File* file, uint8_t* target, const size_t byteCount, const int64_t offset) {
    constexpr size_t kMaximumReadBytes = size_t{1} << 30;
    size_t bytesDone = 0;
    while (bytesDone < byteCount) {
        const size_t wanted = (std::min)(byteCount - bytesDone, kMaximumReadBytes);
        const int64_t bytesRead = mftlib::platform::pread_at(
            file, target + bytesDone, wanted, mftlib::platform::FileOffset{offset + static_cast<int64_t>(bytesDone)});
        if (bytesRead <= 0) {
            return false;
        }
        bytesDone += static_cast<size_t>(bytesRead);
    }
    return true;
}

constexpr size_t kDumpReadParts = 8;

// Reads a chunk of at least two 8 MiB parts as concurrent reads, one per handle, so a chunk the
// page cache holds is copied by several threads instead of one. The calling thread reads the first
// part. Each part but the last is a whole number of 64 KiB units. False when a part's read fails or
// the file ends first; a reader thread that cannot start throws, as any worker of the parse does.
bool ReadChunk(const DumpReadContext& dump, uint8_t* target, const size_t byteCount, const int64_t offset) {
    constexpr size_t kMinimumPartBytes = size_t{8} << 20;
    constexpr size_t kPartAlignment = size_t{64} << 10;
    const size_t partCount = (std::min)(dump.partFiles.size() + 1, byteCount / kMinimumPartBytes);
    if (partCount <= 1) {
        return ReadExactly(dump.input->file, target, byteCount, offset);
    }
    const size_t partBytes = ((byteCount / partCount) + kPartAlignment - 1) / kPartAlignment * kPartAlignment;
    std::array<bool, kDumpReadParts> succeeded{};
    std::array<NativeFailure, kDumpReadParts> failures{};
    std::vector<std::thread> readers;
    readers.reserve(partCount - 1);
    const NativeScopeExit joined([&] {
        for (auto& reader : readers) {
            if (reader.joinable()) {
                reader.join();
            }
        }
    });
    size_t partsRead = 1;
    for (size_t part = 1; part < partCount && part * partBytes < byteCount; part++, partsRead++) {
        const size_t start = part * partBytes;
        const size_t length = (std::min)(partBytes, byteCount - start);
        const auto* partFile = dump.partFiles[part - 1];
        readers.push_back(StartNativeThread(
            [&succeeded, part, partFile, target, start, length, offset] {
                succeeded[part] = ReadExactly(partFile, target + start, length, offset + static_cast<int64_t>(start));
            },
            failures[part]));
    }
    succeeded[0] = ReadExactly(dump.input->file, target, partBytes, offset);
    for (auto& reader : readers) {
        reader.join();
    }
    for (const auto failure : failures) {
        ThrowNativeFailure(failure);
    }
    return std::all_of(succeeded.begin(), succeeded.begin() + static_cast<std::ptrdiff_t>(partsRead),
                       [](const bool value) { return value; });
}

// Loads the next chunk of whole records. A failed or short read ends the input and is
// remembered, so the parse reports it instead of taking it for the end of the file.
uint64_t ReadDumpChunk(void* context, uint8_t* targetBuffer, double& ioMs) {
    auto* dump = static_cast<DumpReadContext*>(context);
    if (dump->recordsRemaining == 0 || dump->readFailed) {
        return 0;
    }
    const uint64_t recordsToLoad = (std::min)(dump->recordsRemaining, static_cast<uint64_t>(dump->bufferSizeRecords));
    const auto byteCount = recordsToLoad * dump->input->geometry.recordSize;
    const auto ioStart = SteadyClock::now();
    if (ShouldFailRead() || !ReadChunk(*dump, targetBuffer, byteCount, dump->fileOffset)) {
        dump->readFailed = true;
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    dump->fileOffset += static_cast<int64_t>(byteCount);
    dump->recordsRemaining -= recordsToLoad;
    return recordsToLoad;
}

// True when a read failed or came up short, or the file is no longer the length that was inspected.
bool DumpReadIncomplete(const void* context) {
    const auto* dump = static_cast<const DumpReadContext*>(context);
    return dump->readFailed || dump->recordsRemaining != 0 ||
           mftlib::platform::size_of(dump->input->file) != static_cast<int64_t>(dump->input->lengthBytes);
}

// Parses every record of an inspected input. The chunk buffer never exceeds the file.
MftParseResult* ParseDump(const MftDumpInput& input, ParseRequest request) {
    const uint64_t totalRecords = input.lengthBytes / input.geometry.recordSize;
    const uint64_t largestChunk = (std::min)(totalRecords, uint64_t{(std::numeric_limits<uint32_t>::max)()});
    request.bufferSizeRecords = static_cast<uint32_t>(std::clamp<uint64_t>(request.bufferSizeRecords, 1, largestChunk));
    DumpReadContext context{&input, totalRecords, request.bufferSizeRecords};
    const NativeScopeExit closeParts([&] {
        for (auto* partFile : context.partFiles) {
            mftlib::platform::close_file(partFile);
        }
    });
    for (size_t part = 1; part < kDumpReadParts; part++) {
        if (auto* partFile = mftlib::platform::reopen_read(input.file); partFile != nullptr) {
            context.partFiles.push_back(partFile);
        }
    }
    const ParseSource source{ReadDumpChunk,      &context, totalRecords,           input.geometry,
                             DumpReadIncomplete, true,     kIncompleteReadMessage, true};
    return ParseMFTImpl(source, request);
}

}  // namespace

extern "C" {
// Opens a dump for one or more parses and reports its length and record size. Returns null,
// with info->errorMessage saying why, when the file cannot be opened or its content is rejected.
// The caller closes a returned input with CloseMftDumpInput. Path is UTF-8 on every platform.
EXPORT MftDumpInput* OpenMftDumpInput(const char* filePathUtf8, MftDumpInputInfo* info) {
    if (info == nullptr) {
        return nullptr;
    }
    *info = MftDumpInputInfo{};
    return NativeBoundary(
        [&]() -> MftDumpInput* {
            auto* file = mftlib::platform::open_read(filePathUtf8);
            const NativeScopeExit opened([&] { mftlib::platform::close_file(file); });
            if (file == nullptr) {
                SetErrorMessage(info->errorMessage, L"Failed to open file. Error: %lu",
                                static_cast<unsigned long>(mftlib::platform::last_error()));
                return nullptr;
            }

            const auto [rejection, invalidInput, lengthBytes, geometry] = InspectDump(file);
            auto* input = rejection != nullptr || ShouldFailAllocation()
                              ? nullptr
                              : new (std::nothrow) MftDumpInput{file, lengthBytes, geometry};
            if (input == nullptr) {
                SetErrorMessage(info->errorMessage,
                                rejection != nullptr ? rejection : L"Failed to allocate dump input");
                info->invalidInput = invalidInput ? 1 : 0;
                return nullptr;
            }

            info->lengthBytes = lengthBytes;
            info->recordSize = geometry.recordSize;
            file = nullptr;
            return input;
        },
        [&](const wchar_t* message) -> MftDumpInput* {
            *info = MftDumpInputInfo{};
            SetErrorMessage(info->errorMessage, message);
            return nullptr;
        });
}

// Parses every allocated base record of an opened dump; freed records are never emitted. Fails with invalidInput set
// when an allocated record's fixup is invalid or the file cannot be read to the length OpenMftDumpInput reported.
EXPORT MftParseResult* ParseMftDumpInput(const MftDumpInput* input, const uint32_t bufferSizeRecords,
                                         const MftParseControl* control, const MftProgressCallback callback,
                                         void* const callbackContext) {
    return NativeBoundary(
        [&] {
            if (input == nullptr) {
                return CreateParseResult(L"Dump input is invalid");
            }
            return ParseDump(*input, ParseRequest{false, bufferSizeRecords, control, callback, callbackContext});
        },
        [](const wchar_t*) -> MftParseResult* { return nullptr; });
}

// As ParseMftDumpInput, but hands each parsed slice to sink as soon as its chunk is parsed and
// returns a result with no entries.
EXPORT MftParseResult* ParseMftDumpInputStreaming(const MftDumpInput* input, const uint32_t bufferSizeRecords,
                                                  const MftParseControl* control, const MftProgressCallback callback,
                                                  void* const callbackContext, const MftParseSink* sink) {
    return NativeBoundary(
        [&] {
            if (input == nullptr || sink == nullptr || sink->slice == nullptr || sink->extensions == nullptr) {
                return CreateParseResult(L"Dump input or sink is invalid");
            }
            return ParseDump(*input, ParseRequest{false, bufferSizeRecords, control, callback, callbackContext, sink});
        },
        [](const wchar_t*) -> MftParseResult* { return nullptr; });
}

// Closes the file and frees the input. Safe with null.
EXPORT void CloseMftDumpInput(MftDumpInput* input) {
    if (const std::unique_ptr<MftDumpInput> owned(input); owned) {
        mftlib::platform::close_file(owned->file);
    }
}
}
