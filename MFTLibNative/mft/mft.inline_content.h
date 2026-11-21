#pragma once

// ReSharper disable once CppUnusedIncludeDirective
// aislop-ignore-next-line CppUnusedIncludeDirective -- size_t below needs this on GCC/Clang
#include <cstddef>
#include <cstdint>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../core/platform.h"
#include "mft.internal.h"

namespace mftlib::inline_content {
// A stretch of the $MFT's data: byteCount bytes of MFT address space starting at mftOffset, stored
// at sourceOffset in whatever the reader reads (a volume, an image or a dump file).
struct Segment {
    uint64_t mftOffset;
    uint64_t byteCount;
    uint64_t sourceOffset;
};

// The largest distance between two wanted records that one read still spans. Records further apart
// start a new read. Chosen by measurement; see the pull request that introduced it.
constexpr uint64_t kCoalesceGapBytes = uint64_t{64} << 10;

// The largest single read, so a request covering the whole MFT is read in bounded pieces.
constexpr uint64_t kMaximumReadBytes = uint64_t{8} << 20;
}  // namespace mftlib::inline_content

// Declared opaque in mft_api.h. The segments are validated by the code that builds them: ascending in
// mftOffset, gap free, and every end inside a signed 64-bit offset.
struct MftInlineReader {
    // Reads up to byteCount bytes at sourceOffset into target from what the reader borrowed and returns how many
    // valid bytes arrived: byteCount for a complete read, fewer for a short one, zero for a failed one.
    using ReadBytesFunction = size_t (*)(const MftInlineReader& reader, uint64_t sourceOffset, uint8_t* target,
                                         size_t byteCount);

    std::vector<mftlib::inline_content::Segment> segments;
    uint64_t totalRecords = 0;
    ParseGeometry geometry{};
    ReadBytesFunction read = nullptr;
    // What read reads: the handle of a volume or image, or the open file of a dump. A reader sets the one
    // its read function uses and borrows it, so it must outlive the reader.
    mftlib::ntfs::detail::RawVolume volume{};
    const mftlib::platform::File* dumpFile = nullptr;
    // The bytes of the group being evaluated, page-aligned so a volume group read lands in them directly. Kept
    // between calls so a long request reuses one allocation.
    mftlib::platform::AlignedBuffer scratch;
};
