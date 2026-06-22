#include "pch.h"

#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/ntfs_io.h"
#include "../core/platform.h"
#include "mft_internal.h"

namespace {

#ifdef _WIN32
struct VolumeReadContext {
    HANDLE volumeHandle;
    std::vector<DataRun>* mftRuns;
    uint32_t bytesPerCluster;
    size_t runIndex;
    uint64_t filesRemaining;
    uint64_t positionInBlock;
    uint32_t bufferSizeRecords;
};

uint64_t VolumeReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* volumeCtx = static_cast<VolumeReadContext*>(ctx);
    while (volumeCtx->filesRemaining == 0) {
        volumeCtx->runIndex++;
        if (volumeCtx->runIndex >= volumeCtx->mftRuns->size()) {
            return 0;
        }
        auto& run = (*volumeCtx->mftRuns)[volumeCtx->runIndex];
        volumeCtx->filesRemaining = run.clusterCount * volumeCtx->bytesPerCluster / FILE_RECORD_SIZE;
        volumeCtx->positionInBlock = 0;
    }

    auto& run = (*volumeCtx->mftRuns)[volumeCtx->runIndex];
    uint64_t filesToLoad = volumeCtx->filesRemaining < static_cast<uint64_t>(volumeCtx->bufferSizeRecords)
                               ? volumeCtx->filesRemaining
                               : static_cast<uint64_t>(volumeCtx->bufferSizeRecords);
    DWORD readBytes;
    auto ioStart = SteadyClock::now();
    if (Read(volumeCtx->volumeHandle, targetBuffer,
             (static_cast<uint64_t>(run.clusterOffset) * volumeCtx->bytesPerCluster) + volumeCtx->positionInBlock,
             static_cast<DWORD>(filesToLoad * FILE_RECORD_SIZE), &readBytes) == 0) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    volumeCtx->positionInBlock += filesToLoad * FILE_RECORD_SIZE;
    volumeCtx->filesRemaining -= filesToLoad;
    return filesToLoad;
}
#endif  // _WIN32

struct FileReadContext {
    mftlib::platform::File* file;
    uint64_t recordsRemaining;
    uint32_t bufferSizeRecords;
    int64_t fileOffset;  // current read position in bytes
};

uint64_t FileReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* fileCtx = static_cast<FileReadContext*>(ctx);
    if (fileCtx->recordsRemaining == 0) {
        return 0;
    }
    uint64_t filesToLoad = fileCtx->recordsRemaining < static_cast<uint64_t>(fileCtx->bufferSizeRecords)
                               ? fileCtx->recordsRemaining
                               : static_cast<uint64_t>(fileCtx->bufferSizeRecords);
    auto byteCount = static_cast<size_t>(filesToLoad * FILE_RECORD_SIZE);
    auto ioStart = SteadyClock::now();
    if (ShouldFailRead()) {
        return 0;
    }
    int64_t bytesRead = mftlib::platform::pread_at(fileCtx->file, targetBuffer, byteCount, fileCtx->fileOffset);
    if (bytesRead <= 0) {
        return 0;
    }
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    uint64_t recordsRead = static_cast<uint64_t>(bytesRead) / FILE_RECORD_SIZE;
    fileCtx->fileOffset += static_cast<int64_t>(recordsRead) * FILE_RECORD_SIZE;
    fileCtx->recordsRemaining -= recordsRead;
    return recordsRead;
}

// Core implementation that takes a UTF-8 file path.
MftParseResult* ParseMFTFromFileImpl(const char* path_utf8, const wchar_t* filter, uint32_t matchFlags,
                                     uint32_t bufferSizeRecords) {
#ifndef _WIN32
    if (filter != nullptr) {
        auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
        if (result) SetErrorMessage(result->errorMessage, L"Filter not supported on Linux yet");
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

    uint64_t totalRecords = static_cast<uint64_t>(fileSize) / FILE_RECORD_SIZE;
    FileReadContext ctx = {file, totalRecords, bufferSizeRecords, 0};
    auto* result = ParseMFTImpl(FileReadChunk, &ctx, totalRecords, filter, matchFlags, bufferSizeRecords);
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
    auto* result = static_cast<MftParseResult*>(calloc(1, sizeof(MftParseResult)));
    if (result == nullptr) {
        return nullptr;
    }

    if (volumeHandle == INVALID_HANDLE_VALUE) {
        SetErrorMessage(result->errorMessage, L"Volume handle is invalid");
        return result;
    }

    NTFS_BPB bpb;
    DWORD bytesRead;
    if ((Read(volumeHandle, &bpb, 0, 512, &bytesRead) == 0) || bytesRead != 512) {
        SetErrorMessage(result->errorMessage, L"Failed to read boot sector. Error: %lu", GetLastError());
        return result;
    }
    if (bpb.name[0] != 'N' || bpb.name[1] != 'T' || bpb.name[2] != 'F' || bpb.name[3] != 'S') {
        SetErrorMessage(result->errorMessage, L"Volume is not NTFS");
        return result;
    }

    uint32_t bytesPerCluster = bpb.bytesPerSector * bpb.sectorsPerCluster;

    uint8_t record0[FILE_RECORD_SIZE];
    if ((Read(volumeHandle, record0, bpb.mftStart * bytesPerCluster, FILE_RECORD_SIZE, &bytesRead) == 0) ||
        bytesRead != FILE_RECORD_SIZE) {
        SetErrorMessage(result->errorMessage, L"Failed to read MFT record 0");
        return result;
    }

    ApplyFixup(record0, FILE_RECORD_SIZE);

    auto* fileRecord0 = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(record0);
    if (fileRecord0->MultiSectorHeader.Magic != 0x454C4946) {
        SetErrorMessage(result->errorMessage, L"Invalid MFT record 0 magic");
        return result;
    }

    auto* dataAttr = FindAttribute(record0, Data);
    if (dataAttr == nullptr) {
        SetErrorMessage(result->errorMessage, L"No Data attribute in MFT record 0");
        return result;
    }
    auto mftRuns = ParseDataRuns(dataAttr);

    auto* attrListAttr = FindAttribute(record0, AttributeList);
    if (attrListAttr != nullptr) {
        uint8_t* attrListData;
        uint64_t attrListSize = 0;

        if (attrListAttr->FormCode == 1) {
            attrListData = ReadNonResidentData(volumeHandle, attrListAttr, bytesPerCluster, &attrListSize);
        } else {
            attrListSize = attrListAttr->Form.Resident.ValueLength;
            attrListData = static_cast<uint8_t*>(malloc(static_cast<size_t>(attrListSize)));
            if (attrListData != nullptr) {
                memcpy(attrListData, reinterpret_cast<uint8_t*>(attrListAttr) + attrListAttr->Form.Resident.ValueOffset,
                       static_cast<size_t>(attrListSize));
            }
        }

        if (attrListData != nullptr) {
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
                    bool found = false;
                    for (auto existing : extensionRecords) {
                        if (existing == segNum) {
                            found = true;
                            break;
                        }
                    }
                    if (!found) {
                        extensionRecords.push_back(segNum);
                    }
                }
                offset += entry->RecordLength;
            }

            uint8_t extRecord[FILE_RECORD_SIZE];
            for (auto recNum : extensionRecords) {
                if (!ReadMFTRecord(volumeHandle, mftRuns, bytesPerCluster, recNum, extRecord)) {
                    continue;
                }
                auto* extHdr = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(extRecord);
                if (extHdr->MultiSectorHeader.Magic != 0x454C4946) {
                    continue;
                }

                auto* extAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(extRecord + extHdr->FirstAttributeOffset);
                while (extAttr->TypeCode != ATTRIBUTE_TYPE_CODE::EndMarker) {
                    if (extAttr->RecordLength == 0) {
                        break;
                    }
                    if (extAttr->TypeCode == Data) {
                        auto additionalRuns = ParseDataRuns(extAttr);
                        for (auto& additionalRun : additionalRuns) {
                            mftRuns.push_back(additionalRun);
                        }
                    }
                    extAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(extAttr) +
                                                                         extAttr->RecordLength);
                }
            }
            free(attrListData);
        }
    }

    uint64_t totalMftBytes = 0;
    for (auto& run : mftRuns) {
        totalMftBytes += run.clusterCount * bytesPerCluster;
    }
    uint64_t totalRecords = totalMftBytes / FILE_RECORD_SIZE;

    VolumeReadContext ctx;
    ctx.volumeHandle = volumeHandle;
    ctx.mftRuns = &mftRuns;
    ctx.bytesPerCluster = bytesPerCluster;
    ctx.runIndex = 0;
    ctx.filesRemaining = mftRuns[0].clusterCount * bytesPerCluster / FILE_RECORD_SIZE;
    ctx.positionInBlock = 0;
    ctx.bufferSizeRecords = bufferSizeRecords;

    free(result);
    return ParseMFTImpl(VolumeReadChunk, &ctx, totalRecords, filter, matchFlags, bufferSizeRecords);
}

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
