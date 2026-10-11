// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.parse.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <limits>
#include <cstdlib>
#include <cstring>
#include <optional>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/platform.h"
#include "mft.internal.h"

#ifndef _WIN32
    #include <unistd.h>
#endif

using namespace mftlib::ntfs;
using namespace mftlib::ntfs::detail;

namespace {

#ifndef _WIN32
const HANDLE INVALID_HANDLE_VALUE = reinterpret_cast<HANDLE>(static_cast<intptr_t>(-1));

// The record size the boot sector declares: a positive clustersPerFileRecord counts clusters, a
// negative one is the base-2 logarithm of the size in bytes.
std::optional<ParseGeometry> ReadBootSectorRecordSize(HANDLE volumeHandle) {
    NTFS_BPB bpb{};
    const auto fileDescriptor = static_cast<int>(reinterpret_cast<intptr_t>(volumeHandle));
    const ssize_t bytesRead =
        RetryInterruptedSystemCall([&]() { return ::pread(fileDescriptor, &bpb, sizeof(bpb), 0); });
    if (bytesRead != static_cast<ssize_t>(sizeof(bpb))) {
        return std::nullopt;
    }
    const auto encoded = static_cast<int8_t>(bpb.clustersPerFileRecord & 0xFFU);
    constexpr int kLargestShift = 31;
    uint64_t recordSize = 0;
    if (encoded < 0 && -encoded <= kLargestShift) {
        recordSize = uint64_t{1} << -encoded;
    } else if (encoded > 0) {
        recordSize = static_cast<uint64_t>(encoded) * bpb.bytesPerSector * bpb.sectorsPerCluster;
    }
    if (recordSize > UINT32_MAX || !IsSupportedRecordSize(static_cast<uint32_t>(recordSize))) {
        return std::nullopt;
    }
    return ParseGeometry{static_cast<uint32_t>(recordSize)};
}
#endif

// The record size override wins. Otherwise Windows asks the file system and, off Windows, where
// the handle is a file descriptor on an NTFS image or device, the boot sector says.
std::optional<ParseGeometry> QueryVolumeRecordSize(HANDLE volumeHandle) {
    uint32_t overrideSize = VolumeRecordSizeOverride();
    if (overrideSize != 0) {
        return IsSupportedRecordSize(overrideSize) ? std::optional<ParseGeometry>(ParseGeometry{overrideSize})
                                                   : std::nullopt;
    }
#ifdef _WIN32
    NTFS_VOLUME_DATA_BUFFER data{};
    DWORD returned = 0;
    if (DeviceIoControl(volumeHandle, FSCTL_GET_NTFS_VOLUME_DATA, nullptr, 0, &data, sizeof(data), &returned,
                        nullptr) == 0 ||
        !IsSupportedRecordSize(data.BytesPerFileRecordSegment)) {
        return std::nullopt;
    }
    return ParseGeometry{data.BytesPerFileRecordSegment};
#else
    return ReadBootSectorRecordSize(volumeHandle);
#endif
}

constexpr wchar_t kMalformedRecordZeroMessage[] = L"MFT record 0 has a malformed attribute";
constexpr wchar_t kRunsOutsideVolumeMessage[] = L"The MFT data runs lie outside the volume";
constexpr wchar_t kMalformedExtensionMessage[] = L"MFT extension record has a malformed attribute";

// A chunk is at least one record, no more records than the MFT holds, and small enough that its
// byte count fits the DWORD a read takes.
uint32_t ClampChunkRecords(uint32_t requested, uint64_t totalRecords, uint32_t recordSize) {
    const uint64_t largest = (std::max)(uint64_t{1}, (std::min)(totalRecords, uint64_t{0xFFFFFFFF} / recordSize));
    return static_cast<uint32_t>(std::clamp<uint64_t>(requested, 1, largest));
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
    bool readFailed = false;
};

// Loads the next batch of whole records. A failed or short read ends the input and is
// remembered, so the parse reports it instead of taking it for the end of the MFT.
uint64_t VolumeReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* volumeCtx = static_cast<VolumeReadContext*>(ctx);
    if (volumeCtx->readFailed) {
        return 0;
    }
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
    const auto requestedBytes = static_cast<DWORD>(filesToLoad * volumeCtx->geometry.recordSize);
    DWORD readBytes = 0;
    auto ioStart = SteadyClock::now();
    if (Read(volumeCtx->volumeHandle, targetBuffer,
             VolumeOffset{(static_cast<uint64_t>(run.clusterOffset) * volumeCtx->bytesPerCluster) +
                          volumeCtx->positionInBlock},
             requestedBytes, &readBytes) == 0 ||
        readBytes != requestedBytes) {
        volumeCtx->readFailed = true;
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    volumeCtx->positionInBlock += filesToLoad * volumeCtx->geometry.recordSize;
    volumeCtx->filesRemaining -= filesToLoad;
    return filesToLoad;
}

bool VolumeReadIncomplete(const void* ctx) { return static_cast<const VolumeReadContext*>(ctx)->readFailed; }

// Collect only unnamed $DATA references, ordered by lowest VCN before de-duplicating
// extension record numbers. Other attributes cannot contribute to the $MFT run list.
std::vector<uint64_t> CollectExtensionRecordNumbers(uint8_t* attrListData, uint64_t attrListSize) {
    std::vector<PATTRIBUTE_LIST_ENTRY> dataEntries;
    uint64_t offset = 0;
    while (offset + sizeof(ATTRIBUTE_LIST_ENTRY) <= attrListSize) {
        auto* entry = reinterpret_cast<PATTRIBUTE_LIST_ENTRY>(attrListData + offset);
        if (entry->RecordLength == 0) {
            break;
        }
        if (entry->AttributeTypeCode == Data && entry->AttributeNameLength == 0) {
            dataEntries.push_back(entry);
        }
        offset += entry->RecordLength;
    }
    std::sort(dataEntries.begin(), dataEntries.end(),
              [](const auto* left, const auto* right) { return left->LowestVcn.QuadPart < right->LowestVcn.QuadPart; });
    std::vector<uint64_t> extensionRecords;
    for (const auto* entry : dataEntries) {
        uint64_t recordNumber = static_cast<uint64_t>(entry->SegmentReference.SegmentNumberLowPart) |
                                (static_cast<uint64_t>(entry->SegmentReference.SegmentNumberHighPart) << 32);
        if (recordNumber != 0 &&
            std::find(extensionRecords.begin(), extensionRecords.end(), recordNumber) == extensionRecords.end()) {
            extensionRecords.push_back(recordNumber);
        }
    }
    return extensionRecords;
}

// Append the unnamed $DATA runs of one validated extension record to mftRuns. Null on success, otherwise
// a failure message with mftRuns unchanged. A malformed attribute chain or invalid runs must never
// drive a later lookup. Named streams contribute no runs.
// totalBytes is the cumulative size of mftRuns, which are already validated; only the new runs are
// measured and added to it, so the merge stays linear in the total number of runs.
const wchar_t* AppendRecordDataRuns(uint8_t* extRecord, uint32_t recordSize, uint32_t bytesPerCluster,
                                    std::vector<DataRun>& mftRuns, uint64_t& totalBytes) {
    const auto* extHdr = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(extRecord);
    std::vector<DataRun> additionalRuns;
    size_t offset = extHdr->FirstAttributeOffset;
    PATTRIBUTE_RECORD_HEADER extAttr = nullptr;
    AttributeStep step = NextAttribute(extRecord, recordSize, &offset, &extAttr);
    while (step == AttributeStep::Attribute) {
        if (extAttr->TypeCode == Data && extAttr->NameLength == 0) {
            auto attributeRuns = ParseDataRuns(extAttr);
            if (extAttr->FormCode == 1 && attributeRuns.empty()) {
                return kMalformedExtensionMessage;
            }
            additionalRuns.insert(additionalRuns.end(), attributeRuns.begin(), attributeRuns.end());
        }
        step = NextAttribute(extRecord, recordSize, &offset, &extAttr);
    }
    if (step != AttributeStep::End) {
        return kMalformedExtensionMessage;
    }
    uint64_t additionalBytes = 0;
    if (!TryMeasureRuns(additionalRuns, bytesPerCluster, &additionalBytes) ||
        additionalBytes > (std::numeric_limits<uint64_t>::max)() - totalBytes) {
        return kRunsOutsideVolumeMessage;
    }
    mftRuns.insert(mftRuns.end(), additionalRuns.begin(), additionalRuns.end());
    totalBytes += additionalBytes;
    return nullptr;
}

// Follow only unnamed $DATA references in ascending lowest VCN, requiring each extension
// to be readable and valid through the runs merged so far; other references are not read.
// Append their validated runs to mftRuns, completing the $MFT's run list. Null on success,
// otherwise the failure message.
// mftRuns arrive validated with cumulative size totalBytes, which grows with each accepted extension.
const wchar_t* MergeExtensionDataRuns(HANDLE volumeHandle, PATTRIBUTE_RECORD_HEADER attrListAttr,
                                      uint32_t bytesPerCluster, ParseGeometry geometry, std::vector<DataRun>& mftRuns,
                                      uint64_t& totalBytes) {
    uint8_t* attrListData;
    uint64_t attrListSize = 0;
    if (attrListAttr->FormCode == 1) {
        attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
    } else {
        constexpr size_t kResidentHeaderSize = 0x18;
        if (attrListAttr->Form.Resident.ValueOffset < kResidentHeaderSize ||
            static_cast<size_t>(attrListAttr->Form.Resident.ValueOffset) + attrListAttr->Form.Resident.ValueLength >
                attrListAttr->RecordLength) {
            return nullptr;
        }
        attrListSize = attrListAttr->Form.Resident.ValueLength;
        if (attrListSize == 0) {
            return nullptr;
        }
        attrListData = static_cast<uint8_t*>(malloc(static_cast<size_t>(attrListSize)));
        if (attrListData != nullptr) {
            memcpy(attrListData, reinterpret_cast<uint8_t*>(attrListAttr) + attrListAttr->Form.Resident.ValueOffset,
                   static_cast<size_t>(attrListSize));
        }
    }
    if (attrListData == nullptr) {
        return kIncompleteVolumeReadMessage;
    }
    const NativeScopeExit attributeData([&]() { free(attrListData); });

    std::vector<uint64_t> extensionRecords = CollectExtensionRecordNumbers(attrListData, attrListSize);
    std::vector<uint8_t> extRecord(geometry.recordSize);
    const wchar_t* failure = nullptr;
    for (auto recNum : extensionRecords) {
        failure = ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, geometry, extRecord.data(), recNum);
        if (failure == nullptr) {
            failure = AppendRecordDataRuns(extRecord.data(), geometry.recordSize, bytesPerCluster, mftRuns, totalBytes);
        }
        if (failure != nullptr) {
            break;
        }
    }
    return failure;
}

// Parses every allocated base record of a volume; includeFreed nonzero also emits freed base records.
MftParseResult* ParseVolume(HANDLE volumeHandle, uint32_t includeFreed, uint32_t bufferSizeRecords,
                            const MftParseControl* control, MftProgressCallback callback, void* context,
                            MftParseResult*& result) {
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
        SetErrorMessage(result->errorMessage, L"Failed to read boot sector. Error: %lu",
                        static_cast<unsigned long>(mftlib::platform::last_error()));
        return result;
    }
    if (bpb.name[0] != 'N' || bpb.name[1] != 'T' || bpb.name[2] != 'F' || bpb.name[3] != 'S') {
        SetErrorMessage(result->errorMessage, L"Volume is not NTFS");
        return result;
    }

    uint32_t bytesPerCluster = bpb.bytesPerSector * bpb.sectorsPerCluster;

    uint64_t mftStartOffset = 0;
    if (!TryClusterByteOffset(bpb.mftStart, bytesPerCluster, &mftStartOffset)) {
        SetErrorMessage(result->errorMessage, L"The MFT start is outside the volume");
        return result;
    }

    std::vector<uint8_t> record0(geometry->recordSize);
    if ((Read(volumeHandle, record0.data(), VolumeOffset{mftStartOffset}, geometry->recordSize, &bytesRead) == 0) ||
        bytesRead != geometry->recordSize) {
        SetErrorMessage(result->errorMessage, L"Failed to read MFT record 0");
        return result;
    }

    const auto* fileRecord0 = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(record0.data());
    if (fileRecord0->MultiSectorHeader.Magic != kFileRecordMagic) {
        SetErrorMessage(result->errorMessage, L"Invalid MFT record 0 magic");
        return result;
    }

    if (!ApplyFixup(record0.data(), geometry->recordSize)) {
        SetErrorMessage(result->errorMessage, L"MFT record 0 has an invalid fixup");
        return result;
    }

    const auto dataSearch = FindAttribute(record0.data(), geometry->recordSize, Data);
    if (dataSearch.malformed) {
        SetErrorMessage(result->errorMessage, kMalformedRecordZeroMessage);
        return result;
    }
    if (dataSearch.attribute == nullptr) {
        SetErrorMessage(result->errorMessage, L"No Data attribute in MFT record 0");
        return result;
    }
    auto mftRuns = ParseDataRuns(dataSearch.attribute);
    if (mftRuns.empty()) {
        SetErrorMessage(result->errorMessage, L"MFT record 0 has no data runs");
        return result;
    }
    uint64_t totalMftBytes = 0;
    if (!TryMeasureRuns(mftRuns, bytesPerCluster, &totalMftBytes)) {
        SetErrorMessage(result->errorMessage, kRunsOutsideVolumeMessage);
        return result;
    }

    const auto attrListSearch = FindAttribute(record0.data(), geometry->recordSize, AttributeList);
    if (attrListSearch.malformed) {
        SetErrorMessage(result->errorMessage, kMalformedRecordZeroMessage);
        return result;
    }
    if (attrListSearch.attribute != nullptr) {
        const auto* failure = MergeExtensionDataRuns(volumeHandle, attrListSearch.attribute, bytesPerCluster, *geometry,
                                                     mftRuns, totalMftBytes);
        if (failure != nullptr) {
            SetErrorMessage(result->errorMessage, failure);
            return result;
        }
    }
    uint64_t totalRecords = totalMftBytes / geometry->recordSize;

    VolumeReadContext ctx;
    ctx.volumeHandle = volumeHandle;
    ctx.mftRuns = &mftRuns;
    ctx.bytesPerCluster = bytesPerCluster;
    ctx.filesRemaining = mftRuns[0].clusterCount * bytesPerCluster / geometry->recordSize;
    ctx.bufferSizeRecords = ClampChunkRecords(bufferSizeRecords, totalRecords, geometry->recordSize);
    ctx.geometry = *geometry;

    free(result);
    result = nullptr;
    const ParseSource source{
        VolumeReadChunk, &ctx, totalRecords, *geometry, VolumeReadIncomplete, false, kIncompleteVolumeReadMessage};
    return ParseMFTImpl(source, ParseRequest{includeFreed != 0, ctx.bufferSizeRecords, control, callback, context});
}

}  // namespace

extern "C" {
EXPORT uint32_t GetMftNativeAbiVersion() { return MFT_NATIVE_ABI_VERSION; }

EXPORT void FreeMftResult(MftParseResult* result) {
    if (result != nullptr) {
        free(result->entries);
        free(result->entryStrings);
        free(result);
    }
}

EXPORT MftParseResult* ParseMFTRecordsWithProgress(HANDLE volumeHandle, uint32_t includeFreed,
                                                   uint32_t bufferSizeRecords, const MftParseControl* control,
                                                   MftProgressCallback callback, void* context) {
    auto* result = CreateParseResult();
    if (result == nullptr) {
        return nullptr;
    }
    return NativeBoundary(
        [&]() {
            return ParseVolume(volumeHandle, includeFreed, bufferSizeRecords, control, callback, context, result);
        },
        [&](const wchar_t* message) {
            if (result != nullptr) {
                SetErrorMessage(result->errorMessage, message);
            }
            return result;
        });
}
}
