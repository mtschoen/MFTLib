// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.parse.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <array>
#include <cstdlib>
#include <cstring>
#include <optional>
#include <string>
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

std::optional<ParseGeometry> DetectRecordSizeFromHeader(const uint8_t* header, size_t headerLength) {
    if (headerLength < 0x20 || memcmp(header, "FILE", 4) != 0) {
        return std::nullopt;
    }
    uint32_t recordSize = 0;
    memcpy(&recordSize, header + 0x1C, sizeof(recordSize));
    return IsSupportedRecordSize(recordSize) ? std::optional<ParseGeometry>(ParseGeometry{recordSize}) : std::nullopt;
}

#ifdef _WIN32
std::optional<ParseGeometry> QueryVolumeRecordSize(HANDLE volumeHandle) {
    uint32_t overrideSize = VolumeRecordSizeOverride();
    if (overrideSize != 0) {
        return IsSupportedRecordSize(overrideSize) ? std::optional<ParseGeometry>(ParseGeometry{overrideSize})
                                                   : std::nullopt;
    }
    NTFS_VOLUME_DATA_BUFFER data{};
    DWORD returned = 0;
    if (DeviceIoControl(volumeHandle, FSCTL_GET_NTFS_VOLUME_DATA, nullptr, 0, &data, sizeof(data), &returned,
                        nullptr) == 0 ||
        !IsSupportedRecordSize(data.BytesPerFileRecordSegment)) {
        return std::nullopt;
    }
    return ParseGeometry{data.BytesPerFileRecordSegment};
}

struct VolumeReadContext {
    HANDLE volumeHandle = INVALID_HANDLE_VALUE;
    std::vector<DataRun>* mftRuns = nullptr;
    uint32_t bytesPerCluster = 0;
    size_t runIndex = 0;
    uint64_t filesRemaining = 0;
    uint64_t positionInBlock = 0;
    uint32_t bufferSizeRecords = 0;
    ParseGeometry geometry{};
};

uint64_t VolumeReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* volumeCtx = static_cast<VolumeReadContext*>(ctx);
    while (volumeCtx->filesRemaining == 0) {
        volumeCtx->runIndex++;
        if (volumeCtx->runIndex >= volumeCtx->mftRuns->size()) {
            return 0;
        }
        auto& run = (*volumeCtx->mftRuns)[volumeCtx->runIndex];
        volumeCtx->filesRemaining = run.clusterCount * volumeCtx->bytesPerCluster / volumeCtx->geometry.recordSize;
        volumeCtx->positionInBlock = 0;
    }

    const auto& run = (*volumeCtx->mftRuns)[volumeCtx->runIndex];
    uint64_t filesToLoad = (std::min)(volumeCtx->filesRemaining, static_cast<uint64_t>(volumeCtx->bufferSizeRecords));
    DWORD readBytes;
    auto ioStart = SteadyClock::now();
    if (Read(volumeCtx->volumeHandle, targetBuffer,
             VolumeOffset{(static_cast<uint64_t>(run.clusterOffset) * volumeCtx->bytesPerCluster) +
                          volumeCtx->positionInBlock},
             static_cast<DWORD>(filesToLoad * volumeCtx->geometry.recordSize), &readBytes) == 0) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    volumeCtx->positionInBlock += filesToLoad * volumeCtx->geometry.recordSize;
    volumeCtx->filesRemaining -= filesToLoad;
    return filesToLoad;
}

// Parse record 0's $ATTRIBUTE_LIST entries into a de-duplicated list of the
// extension record numbers they reference.
std::vector<uint64_t> CollectExtensionRecordNumbers(uint8_t* attrListData, uint64_t attrListSize) {
    std::vector<uint64_t> extensionRecords;
    uint64_t offset = 0;
    while (offset + sizeof(ATTRIBUTE_LIST_ENTRY) <= attrListSize) {
        auto* entry = reinterpret_cast<PATTRIBUTE_LIST_ENTRY>(attrListData + offset);
        if (entry->RecordLength == 0) {
            break;
        }
        uint64_t segNum = static_cast<uint64_t>(entry->SegmentReference.SegmentNumberLowPart) |
                          (static_cast<uint64_t>(entry->SegmentReference.SegmentNumberHighPart) << 32);
        if (segNum != 0) {
            if (std::find(extensionRecords.begin(), extensionRecords.end(), segNum) == extensionRecords.end()) {
                extensionRecords.push_back(segNum);
            }
        }
        offset += entry->RecordLength;
    }
    return extensionRecords;
}

// Append the $DATA runs of one already-read extension record to mftRuns.
void AppendRecordDataRuns(uint8_t* extRecord, std::vector<DataRun>& mftRuns) {
    const auto* extHdr = reinterpret_cast<const PFILE_RECORD_SEGMENT_HEADER>(extRecord);
    if (extHdr->MultiSectorHeader.Magic != 0x454C4946) {
        return;
    }
    auto* extAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(extRecord + extHdr->FirstAttributeOffset);
    while (extAttr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
        if (extAttr->RecordLength == 0) {
            break;
        }
        if (extAttr->TypeCode == Data) {
            auto additionalRuns = ParseDataRuns(extAttr);
            for (const auto& additionalRun : additionalRuns) {
                mftRuns.push_back(additionalRun);
            }
        }
        extAttr =
            reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(extAttr) + extAttr->RecordLength);
    }
}

// Follow record 0's $ATTRIBUTE_LIST (when present) to its extension records and
// append their $DATA runs to mftRuns, completing the $MFT's run list.
void MergeExtensionDataRuns(HANDLE volumeHandle, PATTRIBUTE_RECORD_HEADER attrListAttr, uint32_t bytesPerCluster,
                            ParseGeometry geometry, std::vector<DataRun>& mftRuns) {
    uint8_t* attrListData;
    uint64_t attrListSize = 0;
    if (attrListAttr->FormCode == 1) {
        attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
    } else {
        constexpr size_t kResidentHeaderSize = 0x18;
        if (attrListAttr->Form.Resident.ValueOffset < kResidentHeaderSize ||
            static_cast<size_t>(attrListAttr->Form.Resident.ValueOffset) + attrListAttr->Form.Resident.ValueLength >
                attrListAttr->RecordLength) {
            return;
        }
        attrListSize = attrListAttr->Form.Resident.ValueLength;
        attrListData = static_cast<uint8_t*>(malloc(static_cast<size_t>(attrListSize)));
        if (attrListData != nullptr) {
            memcpy(attrListData, reinterpret_cast<uint8_t*>(attrListAttr) + attrListAttr->Form.Resident.ValueOffset,
                   static_cast<size_t>(attrListSize));
        }
    }
    if (attrListData == nullptr) {
        return;
    }

    std::vector<uint64_t> extensionRecords = CollectExtensionRecordNumbers(attrListData, attrListSize);
    std::vector<uint8_t> extRecord(geometry.recordSize);
    for (auto recNum : extensionRecords) {
        if (ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, geometry, extRecord.data(), recNum)) {
            AppendRecordDataRuns(extRecord.data(), mftRuns);
        }
    }
    free(attrListData);
}
#endif  // _WIN32

struct FileReadContext {
    mftlib::platform::File* file = nullptr;
    uint64_t recordsRemaining = 0;
    uint32_t bufferSizeRecords = 0;
    int64_t fileOffset = 0;  // current read position in bytes
    ParseGeometry geometry{};
};

uint64_t FileReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* fileCtx = static_cast<FileReadContext*>(ctx);
    if (fileCtx->recordsRemaining == 0) {
        return 0;
    }
    uint64_t filesToLoad = (std::min)(fileCtx->recordsRemaining, static_cast<uint64_t>(fileCtx->bufferSizeRecords));
    auto byteCount = static_cast<size_t>(filesToLoad * fileCtx->geometry.recordSize);
    auto ioStart = SteadyClock::now();
    if (ShouldFailRead()) {
        return 0;
    }
    int64_t bytesRead = mftlib::platform::pread_at(fileCtx->file, targetBuffer, byteCount,
                                                   mftlib::platform::FileOffset{fileCtx->fileOffset});
    if (bytesRead <= 0) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    uint64_t recordsRead = static_cast<uint64_t>(bytesRead) / fileCtx->geometry.recordSize;
    fileCtx->fileOffset += static_cast<int64_t>(recordsRead * fileCtx->geometry.recordSize);
    fileCtx->recordsRemaining -= recordsRead;
    return recordsRead;
}

// Core implementation that takes a UTF-8 file path.
MftParseResult* ParseMFTFromFileImpl(const char* path_utf8, const wchar_t* filter, uint32_t matchFlags,
                                     uint32_t bufferSizeRecords) {
#ifndef _WIN32
    if (filter != nullptr) {
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
        if (result != nullptr) {
            SetErrorMessage(result->errorMessage, L"Filter not supported on Linux yet");
        }
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

    if (fileSize == 0) {
        mftlib::platform::close_file(file);
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
        return result;
    }

    std::array<uint8_t, 0x20> header{};
    int64_t headerBytesRead =
        mftlib::platform::pread_at(file, header.data(), header.size(), mftlib::platform::FileOffset{0});
    auto geometry = (headerBytesRead >= static_cast<int64_t>(header.size()))
                        ? DetectRecordSizeFromHeader(header.data(), header.size())
                        : std::nullopt;
    if (!geometry.has_value()) {
        mftlib::platform::close_file(file);
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
        if (result != nullptr) {
            SetErrorMessage(result->errorMessage, L"Invalid or unsupported MFT record size");
        }
        return result;
    }

    if (static_cast<uint64_t>(fileSize) % geometry->recordSize != 0) {
        mftlib::platform::close_file(file);
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
        if (result != nullptr) {
            SetErrorMessage(result->errorMessage, L"File size is not a whole multiple of record size");
        }
        return result;
    }

    uint64_t totalRecords = static_cast<uint64_t>(fileSize) / geometry->recordSize;
    FileReadContext ctx = {file, totalRecords, bufferSizeRecords, 0, *geometry};
    auto* result = ParseMFTImpl(FileReadChunk, &ctx, totalRecords, FilterSpec{filter, 0, matchFlags}, bufferSizeRecords,
                                *geometry);
    mftlib::platform::close_file(file);
    return result;
}

}  // namespace

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
    auto* result = ShouldFailAlloc() ? nullptr : static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
    if (result == nullptr) {
        return nullptr;
    }

    if (volumeHandle == INVALID_HANDLE_VALUE) {
        SetErrorMessage(result->errorMessage, L"Volume handle is invalid");
        return result;
    }

    auto geometry = QueryVolumeRecordSize(volumeHandle);
    if (!geometry.has_value()) {
        SetErrorMessage(result->errorMessage, L"Failed to query volume record size or unsupported record size");
        return result;
    }

    NTFS_BPB bpb;
    DWORD bytesRead;
    if ((Read(volumeHandle, &bpb, VolumeOffset{0}, 512, &bytesRead) == 0) || bytesRead != 512) {
        SetErrorMessage(result->errorMessage, L"Failed to read boot sector. Error: %lu", GetLastError());
        return result;
    }
    if (bpb.name[0] != 'N' || bpb.name[1] != 'T' || bpb.name[2] != 'F' || bpb.name[3] != 'S') {
        SetErrorMessage(result->errorMessage, L"Volume is not NTFS");
        return result;
    }

    uint32_t bytesPerCluster = bpb.bytesPerSector * bpb.sectorsPerCluster;

    std::vector<uint8_t> record0(geometry->recordSize);
    if ((Read(volumeHandle, record0.data(), VolumeOffset{bpb.mftStart * bytesPerCluster}, geometry->recordSize,
              &bytesRead) == 0) ||
        bytesRead != geometry->recordSize) {
        SetErrorMessage(result->errorMessage, L"Failed to read MFT record 0");
        return result;
    }

    ApplyFixup(record0.data(), geometry->recordSize);

    const auto* fileRecord0 = reinterpret_cast<const PFILE_RECORD_SEGMENT_HEADER>(record0.data());
    if (fileRecord0->MultiSectorHeader.Magic != 0x454C4946) {
        SetErrorMessage(result->errorMessage, L"Invalid MFT record 0 magic");
        return result;
    }

    const auto* dataAttr = FindAttribute(record0.data(), Data);
    if (dataAttr == nullptr) {
        SetErrorMessage(result->errorMessage, L"No Data attribute in MFT record 0");
        return result;
    }
    auto mftRuns = ParseDataRuns(dataAttr);

    auto* attrListAttr = FindAttribute(record0.data(), AttributeList);
    if (attrListAttr != nullptr) {
        MergeExtensionDataRuns(volumeHandle, attrListAttr, bytesPerCluster, *geometry, mftRuns);
    }

    uint64_t totalMftBytes = 0;
    for (const auto& run : mftRuns) {
        totalMftBytes += run.clusterCount * bytesPerCluster;
    }
    uint64_t totalRecords = totalMftBytes / geometry->recordSize;

    VolumeReadContext ctx;
    ctx.volumeHandle = volumeHandle;
    ctx.mftRuns = &mftRuns;
    ctx.bytesPerCluster = bytesPerCluster;
    ctx.runIndex = 0;
    ctx.filesRemaining = mftRuns[0].clusterCount * bytesPerCluster / geometry->recordSize;
    ctx.positionInBlock = 0;
    ctx.bufferSizeRecords = bufferSizeRecords;
    ctx.geometry = *geometry;

    free(result);
    return ParseMFTImpl(VolumeReadChunk, &ctx, totalRecords, FilterSpec{filter, 0, matchFlags}, bufferSizeRecords,
                        *geometry);
}

// C-ABI export; (filePath, filter) order is fixed by the C# P/Invoke signature.
// NOLINTNEXTLINE(bugprone-easily-swappable-parameters)
EXPORT MftParseResult* ParseMFTFromFile(const wchar_t* filePath, const wchar_t* filter, uint32_t matchFlags,
                                        uint32_t bufferSizeRecords) {
    int u8len =
        ShouldFailPathConversion() ? 0 : WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    if (u8len <= 0) {
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
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
