// The file side of the MFT parser: a dump (a saved $MFT image) is opened once, inspected, and
// parsed through that one open file. A dump is untrusted input, so nothing read from it is
// believed before it is checked, and a file that fails a check is rejected, never repaired.
#include "pch.h"

#include <algorithm>
#include <array>
// aislop-ignore-next-line CppUnusedIncludeDirective -- memcpy and memcmp below need this on GCC/Clang
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <string>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/platform.h"
#include "mft.internal.h"

#ifdef _WIN32
    #include <stringapiset.h>
#endif

// One opened dump file with the length and record size inspection found. A parse reads this
// file and no other, so replacing the path after the open cannot change what is parsed.
// Declared opaque in mft_api.h because the exports below hand it to the caller.
struct MftDumpInput {
    mftlib::platform::File* file = nullptr;
    uint64_t lengthBytes = 0;
    ParseGeometry geometry{};
};

namespace {

const wchar_t* const kIncompleteReadMessage = L"The dump file could not be read completely.";

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
};

// Reads exactly byteCount bytes at offset, in calls small enough for every platform read.
// False when a read fails or the file ends first.
bool ReadExactly(const mftlib::platform::File* file, uint8_t* target, size_t byteCount, int64_t offset) {
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

// Loads the next chunk of whole records. A failed or short read ends the input and is
// remembered, so the parse reports it instead of taking it for the end of the file.
uint64_t ReadDumpChunk(void* context, uint8_t* targetBuffer, double& ioMs) {
    auto* dump = static_cast<DumpReadContext*>(context);
    if (dump->recordsRemaining == 0 || dump->readFailed) {
        return 0;
    }
    const uint64_t recordsToLoad = (std::min)(dump->recordsRemaining, static_cast<uint64_t>(dump->bufferSizeRecords));
    const auto byteCount = static_cast<size_t>(recordsToLoad * dump->input->geometry.recordSize);
    const auto ioStart = SteadyClock::now();
    if (ShouldFailRead() || !ReadExactly(dump->input->file, targetBuffer, byteCount, dump->fileOffset)) {
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
    const ParseSource source{ReadDumpChunk, &context, totalRecords, input.geometry, DumpReadIncomplete, true};
    return ParseMFTImpl(source, request);
}

MftParseResult* CreateOpenFailureResult() {
    auto* result = CreateParseResult();
    if (result != nullptr) {
        SetErrorMessage(result->errorMessage, L"Failed to open file. Error: %lu",
                        static_cast<unsigned long>(mftlib::platform::last_error()));
    }
    return result;
}

MftParseResult* CreateRejectionResult(const DumpInspection& inspection) {
    auto* result = CreateParseResult(inspection.rejection);
    if (result != nullptr) {
        result->invalidInput = inspection.invalidInput ? 1 : 0;
    }
    return result;
}

// Opens, inspects, parses and closes one file. Path is UTF-8.
MftParseResult* ParseDumpFile(const char* pathUtf8, const ParseRequest& request) {
#ifndef _WIN32
    if (request.filter.text != nullptr) {
        return CreateParseResult(L"Filter not supported on Linux yet");
    }
#endif

    auto* file = mftlib::platform::open_read(pathUtf8);
    if (file == nullptr) {
        return CreateOpenFailureResult();
    }

    const DumpInspection inspection = InspectDump(file);
    MftParseResult* result = inspection.rejection != nullptr
                                 ? CreateRejectionResult(inspection)
                                 : ParseDump(MftDumpInput{file, inspection.lengthBytes, inspection.geometry}, request);
    mftlib::platform::close_file(file);
    return result;
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

    auto* file = mftlib::platform::open_read(filePathUtf8);
    if (file == nullptr) {
        SetErrorMessage(info->errorMessage, L"Failed to open file. Error: %lu",
                        static_cast<unsigned long>(mftlib::platform::last_error()));
        return nullptr;
    }

    const DumpInspection inspection = InspectDump(file);
    auto* input = (inspection.rejection != nullptr || ShouldFailAlloc())
                      ? nullptr
                      : new (std::nothrow) MftDumpInput{file, inspection.lengthBytes, inspection.geometry};
    if (input == nullptr) {
        mftlib::platform::close_file(file);
        SetErrorMessage(info->errorMessage,
                        inspection.rejection != nullptr ? inspection.rejection : L"Failed to allocate dump input");
        info->invalidInput = inspection.invalidInput ? 1 : 0;
        return nullptr;
    }

    info->lengthBytes = inspection.lengthBytes;
    info->recordSize = inspection.geometry.recordSize;
    return input;
}

// Parses every allocated base record of an opened dump, without a name filter or path
// resolution. Fails with invalidInput set when an allocated record's fixup is invalid or the
// file cannot be read to the length OpenMftDumpInput reported.
EXPORT MftParseResult* ParseMftDumpInput(const MftDumpInput* input, uint32_t bufferSizeRecords,
                                         const MftParseControl* control, MftProgressCallback callback, void* context) {
    if (input == nullptr) {
        return CreateParseResult(L"Dump input is invalid");
    }
    return ParseDump(
        *input, ParseRequest{FilterSpec{nullptr, 0, MATCH_FLAG_NONE}, bufferSizeRecords, control, callback, context});
}

// Closes the file and frees the input. Safe with null.
EXPORT void CloseMftDumpInput(MftDumpInput* input) {
    const std::unique_ptr<MftDumpInput> owned(input);
    if (owned) {
        mftlib::platform::close_file(owned->file);
    }
}

#ifdef _WIN32
// C-ABI export; (filePath, filter) order is fixed by the C# P/Invoke signature.
// NOLINTNEXTLINE(bugprone-easily-swappable-parameters)
EXPORT MftParseResult* ParseMFTFromFile(const wchar_t* filePath, const wchar_t* filter, uint32_t matchFlags,
                                        uint32_t bufferSizeRecords, const MftParseControl* control,
                                        MftProgressCallback callback, void* context) {
    int u8len =
        ShouldFailPathConversion() ? 0 : WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    if (u8len <= 0) {
        return CreateParseResult(L"Failed to convert path to UTF-8");
    }
    std::string utf8(static_cast<size_t>(u8len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8.data(), u8len, nullptr, nullptr);
    return ParseDumpFile(
        utf8.c_str(), ParseRequest{FilterSpec{filter, 0, matchFlags}, bufferSizeRecords, control, callback, context});
}
#else
EXPORT MftParseResult* ParseMFTFromFileUtf8(const char* filePath, const wchar_t* filter, uint32_t matchFlags,
                                            uint32_t bufferSizeRecords) {
    return ParseDumpFile(filePath, ParseRequest{FilterSpec{filter, 0, matchFlags}, bufferSizeRecords});
}

EXPORT MftParseResult* ParseMFTFromFileUtf8WithProgress(const char* filePath, const wchar_t* filter,
                                                        uint32_t matchFlags, uint32_t bufferSizeRecords,
                                                        const MftParseControl* control, MftProgressCallback callback,
                                                        void* context) {
    return ParseDumpFile(
        filePath, ParseRequest{FilterSpec{filter, 0, matchFlags}, bufferSizeRecords, control, callback, context});
}
#endif
}
