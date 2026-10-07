// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.parse.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <cstdlib>
#include <cstring>
#include <optional>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "mft.internal.h"

using namespace mftlib::ntfs;
using namespace mftlib::ntfs::detail;

namespace {

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
    if (extHdr->MultiSectorHeader.Magic != kFileRecordMagic) {
        return;
    }
    auto* extAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(extRecord + extHdr->FirstAttributeOffset);
    while (extAttr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
        if (extAttr->RecordLength == 0) {
            break;
        }
        if (extAttr->TypeCode == Data) {
            auto additionalRuns = ParseDataRuns(extAttr);
            mftRuns.insert(mftRuns.end(), additionalRuns.begin(), additionalRuns.end());
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

}  // namespace

extern "C" {
EXPORT uint32_t GetMftNativeAbiVersion() { return MFT_NATIVE_ABI_VERSION; }

EXPORT void FreeMftResult(MftParseResult* result) {
    if (result != nullptr) {
        free(result->entries);
        free(result->pathEntries);
        free(result->entryStrings);
        free(result->pathStrings);
        free(result);
    }
}

#ifdef _WIN32
EXPORT MftParseResult* ParseMFTRecordsWithProgress(HANDLE volumeHandle, const wchar_t* filter, uint32_t matchFlags,
                                                   uint32_t bufferSizeRecords, const MftParseControl* control,
                                                   MftProgressCallback callback, void* context) {
    auto* result = CreateParseResult();
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

    const auto* fileRecord0 = reinterpret_cast<const PFILE_RECORD_SEGMENT_HEADER>(record0.data());
    if (fileRecord0->MultiSectorHeader.Magic != kFileRecordMagic) {
        SetErrorMessage(result->errorMessage, L"Invalid MFT record 0 magic");
        return result;
    }

    if (!ApplyFixup(record0.data(), geometry->recordSize)) {
        SetErrorMessage(result->errorMessage, L"MFT record 0 has an invalid fixup");
        return result;
    }

    const auto* dataAttr = FindAttribute(record0.data(), Data);
    if (dataAttr == nullptr) {
        SetErrorMessage(result->errorMessage, L"No Data attribute in MFT record 0");
        return result;
    }
    auto mftRuns = ParseDataRuns(dataAttr);
    if (mftRuns.empty()) {
        SetErrorMessage(result->errorMessage, L"MFT record 0 has no data runs");
        return result;
    }

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
    ctx.filesRemaining = mftRuns[0].clusterCount * bytesPerCluster / geometry->recordSize;
    ctx.bufferSizeRecords = bufferSizeRecords;
    ctx.geometry = *geometry;

    free(result);
    const ParseSource source{VolumeReadChunk, &ctx, totalRecords, *geometry};
    return ParseMFTImpl(source,
                        ParseRequest{FilterSpec{filter, 0, matchFlags}, bufferSizeRecords, control, callback, context});
}
#endif  // _WIN32

#ifndef _WIN32
EXPORT MftParseResult* ParseMFTRecordsWithProgress(void* /*volumeHandle*/, const wchar_t* /*filter*/,
                                                   uint32_t /*matchFlags*/, uint32_t /*bufferSizeRecords*/,
                                                   const MftParseControl* /*control*/, MftProgressCallback /*callback*/,
                                                   void* /*context*/) {
    return CreateParseResult(L"Direct volume parsing is not supported on Linux");
}
#endif
}
