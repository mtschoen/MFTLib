// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.inline_volume.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <memory>
#include <new>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "mft.internal.h"
#include "mft.inline_content.h"

namespace {

// Reads from the volume or image handle the reader borrowed: an image read that reaches its end comes up short, and a
// failed read returns nothing.
size_t ReadVolumeBytes(const MftInlineReader& reader, uint64_t sourceOffset, uint8_t* target, size_t byteCount) {
    DWORD bytesRead = 0;
    return Read(reader.volumeHandle, target, VolumeOffset{sourceOffset}, static_cast<DWORD>(byteCount), &bytesRead) != 0
               ? bytesRead
               : 0;
}

// One segment per validated run of the $MFT, in MFT order.
std::vector<mftlib::inline_content::Segment> SegmentsOf(const VolumeLayout& layout) {
    std::vector<mftlib::inline_content::Segment> segments;
    segments.reserve(layout.mftRuns.size());
    uint64_t mftOffset = 0;
    for (const auto& run : layout.mftRuns) {
        const uint64_t byteCount = run.clusterCount * layout.bytesPerCluster;
        segments.push_back({mftOffset, byteCount, static_cast<uint64_t>(run.clusterOffset) * layout.bytesPerCluster});
        mftOffset += byteCount;
    }
    return segments;
}

MftInlineReader* OpenVolumeInlineReader(HANDLE volumeHandle, MftInlineReaderInfo* info) {
    VolumeLayout layout;
    if (!LoadVolumeLayout(volumeHandle, &layout, info->errorMessage)) {
        return nullptr;
    }
    std::unique_ptr<MftInlineReader> reader(ShouldFailAllocation() ? nullptr : new (std::nothrow) MftInlineReader());
    if (reader == nullptr) {
        SetErrorMessage(info->errorMessage, L"Failed to allocate inline content reader");
        return nullptr;
    }
    reader->segments = SegmentsOf(layout);
    reader->geometry = layout.geometry;
    reader->totalRecords = layout.totalMftBytes / layout.geometry.recordSize;
    reader->read = ReadVolumeBytes;
    reader->volumeHandle = volumeHandle;
    info->totalRecords = reader->totalRecords;
    info->recordSize = layout.geometry.recordSize;
    return reader.release();
}

}  // namespace

extern "C" {
// Opens a reader on the MFT of an NTFS volume or image, locating the $MFT's runs once. Returns null, with
// info->errorMessage saying why, when the handle is not a readable NTFS volume. The reader borrows the
// handle, which must stay open until CloseMftInlineReader.
EXPORT MftInlineReader* OpenMftInlineReader(HANDLE volumeHandle, MftInlineReaderInfo* info) {
    if (info == nullptr) {
        return nullptr;
    }
    *info = MftInlineReaderInfo{};
    return NativeBoundary([&] { return OpenVolumeInlineReader(volumeHandle, info); },
                          [&](const wchar_t* message) -> MftInlineReader* {
                              SetErrorMessage(info->errorMessage, message);
                              return nullptr;
                          });
}
}
