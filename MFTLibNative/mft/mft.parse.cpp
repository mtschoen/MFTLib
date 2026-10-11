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
    if (::pread(fileDescriptor, &bpb, sizeof(bpb), 0) != static_cast<ssize_t>(sizeof(bpb))) {
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

// Append the $DATA runs of one already-read extension record to mftRuns. A record whose attribute
// chain leaves the record contributes nothing. False, with mftRuns unchanged, when the runs it
// declares would leave the volume or overflow the $MFT's size: no later lookup may use them.
// totalBytes is the cumulative size of mftRuns, which are already validated; only the new runs are
// measured and added to it, so the merge stays linear in the total number of runs.
bool AppendRecordDataRuns(uint8_t* extRecord, uint32_t recordSize, uint32_t bytesPerCluster,
                          std::vector<DataRun>& mftRuns, uint64_t& totalBytes) {
    const auto* extHdr = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(extRecord);
    if (extHdr->MultiSectorHeader.Magic != kFileRecordMagic) {
        return true;
    }
    std::vector<DataRun> additionalRuns;
    size_t offset = extHdr->FirstAttributeOffset;
    PATTRIBUTE_RECORD_HEADER extAttr = nullptr;
    AttributeStep step = NextAttribute(extRecord, recordSize, &offset, &extAttr);
    while (step == AttributeStep::Attribute) {
        if (extAttr->TypeCode == Data) {
            auto attributeRuns = ParseDataRuns(extAttr);
            additionalRuns.insert(additionalRuns.end(), attributeRuns.begin(), attributeRuns.end());
        }
        step = NextAttribute(extRecord, recordSize, &offset, &extAttr);
    }
    if (step != AttributeStep::End) {
        return true;
    }
    uint64_t additionalBytes = 0;
    if (!TryMeasureRuns(additionalRuns, bytesPerCluster, &additionalBytes) ||
        additionalBytes > (std::numeric_limits<uint64_t>::max)() - totalBytes) {
        return false;
    }
    mftRuns.insert(mftRuns.end(), additionalRuns.begin(), additionalRuns.end());
    totalBytes += additionalBytes;
    return true;
}

// Follow record 0's $ATTRIBUTE_LIST (when present) to its extension records and
// append their $DATA runs to mftRuns, completing the $MFT's run list. An extension record that cannot
// be read or found is skipped; one whose runs lie outside the volume stops the merge and returns false.
// mftRuns arrive validated with cumulative size totalBytes, which grows with each accepted extension.
bool MergeExtensionDataRuns(HANDLE volumeHandle, PATTRIBUTE_RECORD_HEADER attrListAttr, uint32_t bytesPerCluster,
                            ParseGeometry geometry, std::vector<DataRun>& mftRuns, uint64_t& totalBytes) {
    uint8_t* attrListData;
    uint64_t attrListSize = 0;
    if (attrListAttr->FormCode == 1) {
        attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
    } else {
        constexpr size_t kResidentHeaderSize = 0x18;
        if (attrListAttr->Form.Resident.ValueOffset < kResidentHeaderSize ||
            static_cast<size_t>(attrListAttr->Form.Resident.ValueOffset) + attrListAttr->Form.Resident.ValueLength >
                attrListAttr->RecordLength) {
            return true;
        }
        attrListSize = attrListAttr->Form.Resident.ValueLength;
        attrListData = static_cast<uint8_t*>(malloc(static_cast<size_t>(attrListSize)));
        if (attrListData != nullptr) {
            memcpy(attrListData, reinterpret_cast<uint8_t*>(attrListAttr) + attrListAttr->Form.Resident.ValueOffset,
                   static_cast<size_t>(attrListSize));
        }
    }
    if (attrListData == nullptr) {
        return true;
    }

    std::vector<uint64_t> extensionRecords = CollectExtensionRecordNumbers(attrListData, attrListSize);
    std::vector<uint8_t> extRecord(geometry.recordSize);
    bool merged = true;
    for (auto recNum : extensionRecords) {
        if (ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, geometry, extRecord.data(), recNum) &&
            !AppendRecordDataRuns(extRecord.data(), geometry.recordSize, bytesPerCluster, mftRuns, totalBytes)) {
            merged = false;
            break;
        }
    }
    free(attrListData);
    return merged;
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

// Parses every allocated base record of a volume; includeFreed nonzero also emits freed base records.
EXPORT MftParseResult* ParseMFTRecordsWithProgress(HANDLE volumeHandle, uint32_t includeFreed,
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
        if (!MergeExtensionDataRuns(volumeHandle, attrListSearch.attribute, bytesPerCluster, *geometry, mftRuns,
                                    totalMftBytes)) {
            SetErrorMessage(result->errorMessage, kRunsOutsideVolumeMessage);
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
    const ParseSource source{VolumeReadChunk,
                             &ctx,
                             totalRecords,
                             *geometry,
                             VolumeReadIncomplete,
                             false,
                             L"The volume could not be read completely."};
    return ParseMFTImpl(source, ParseRequest{includeFreed != 0, ctx.bufferSizeRecords, control, callback, context});
}
}
