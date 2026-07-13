#include "pch.h"

#include <array>
// aislop-ignore-next-line CppUnusedIncludeDirective -- memcpy/memset below need this on GCC/Clang
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../internal.h"
#include "../core/platform.h"

#ifdef _WIN32
    #include <stringapiset.h>
#endif

namespace {

constexpr std::array<const wchar_t*, 16> fileNames = {
    L"README.md",    L"index.html", L"main.cpp",   L"package.json", L"Makefile",  L"config.yaml",
    L"data.bin",     L"icon.png",   L"setup.py",   L"app.js",       L"style.css", L"test.go",
    L"build.gradle", L"Cargo.toml", L"Program.cs", L"pom.xml",
};
constexpr std::array<const wchar_t*, 16> dirNames = {
    L"src", L"bin",     L"obj",    L"node_modules", L".git",   L"build", L"docs", L"tests",
    L"lib", L"include", L"assets", L"scripts",      L"config", L"data",  L"temp", L"cache",
};
constexpr int numFileNames = 16;
constexpr int numDirNames = 16;

// Descriptor for one synthetic record, bundled so the several u64/u16 fields
// cannot be transposed at the call site.
struct SyntheticRecordSpec {
    uint64_t recordIndex;
    uint64_t parentRecord;
    uint16_t flags;
    const wchar_t* name;
    uint8_t nameLen;
    uint64_t baseRef;
};

// Work assignment for one buffer batch.
struct BatchParams {
    uint64_t batchSize;
    uint64_t baseIndex;
    unsigned threadCount;
};

// Strong type for a total record count, kept distinct from the per-buffer size.
struct RecordCount {
    uint64_t value;
};

void ApplyUSAProtection(uint8_t* record, uint16_t usn) {
    auto* header = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(record);
    uint16_t usaOffset = header->MultiSectorHeader.UpdateSequenceArrayOffset;
    uint16_t usaSize = header->MultiSectorHeader.UpdateSequenceArraySize;
    auto* usa = reinterpret_cast<uint16_t*>(record + usaOffset);
    usa[0] = usn;

    uint16_t sectorCount = usaSize - 1;
    for (uint16_t i = 0; i < sectorCount; i++) {
        uint32_t sectorEnd = ((i + 1) * 512) - 2;
        auto* sectorLastWord = reinterpret_cast<uint16_t*>(record + sectorEnd);
        usa[i + 1] = *sectorLastWord;
        *sectorLastWord = usn;
    }
}

// Narrow a wchar_t literal/string to NTFS WCHAR (UTF-16, char16_t units).
// On Windows wchar_t is 16-bit so this is a direct copy.
// On Linux wchar_t is 32-bit; we truncate each unit to 16 bits.
void StoreNtfsName(WCHAR* dst, uint16_t dstCapacity, const wchar_t* src, uint16_t srcLen) {
    uint16_t copyLen = srcLen < dstCapacity ? srcLen : dstCapacity;
    for (uint16_t i = 0; i < copyLen; i++) {
        auto unit = static_cast<uint16_t>(src[i]);
        memcpy(dst + i, &unit, sizeof(uint16_t));
    }
}

// Timestamps, sizes, and attribute flags for one synthetic record, rolled from
// the per-record PRNG. The exact order of nextRng() calls is load-bearing: it
// determines record contents the parser-side tests assert on.
struct SyntheticMeta {
    uint64_t createTime;
    uint64_t modTime;
    uint64_t mftModTime;
    uint64_t readTime;
    uint64_t fileSize;
    uint64_t allocSize;
    uint32_t fileAttrs;
    bool isDir;
};

SyntheticMeta RollSyntheticMeta(uint16_t flags, uint64_t* rng) {
    auto nextRng = [rng]() -> uint64_t {
        *rng ^= *rng << 13;
        *rng ^= *rng >> 7;
        *rng ^= *rng << 17;
        return *rng;
    };

    constexpr uint64_t ftBase2015 = 130645440000000000ULL;
    constexpr uint64_t tenYearsTicks = 10ULL * 365 * 24 * 3600 * 10000000ULL;
    uint64_t createTime = ftBase2015 + (nextRng() % tenYearsTicks);
    uint64_t modTime = createTime + (nextRng() % (tenYearsTicks / 10));
    uint64_t mftModTime = modTime + (nextRng() % 100000000ULL);
    uint64_t readTime = modTime + (nextRng() % (tenYearsTicks / 5));

    bool isDir = (flags & 0x0002) != 0;
    uint64_t fileSize = isDir ? 0 : (nextRng() % (256ULL * 1024 * 1024));
    uint64_t allocSize = (fileSize + 4095) & ~4095ULL;
    uint32_t fileAttrs = isDir ? 0x10 : 0x20;
    if (nextRng() % 20 == 0) {
        fileAttrs |= 0x02;
    }
    if (nextRng() % 50 == 0) {
        fileAttrs |= 0x04;
    }
    if (nextRng() % 10 == 0) {
        fileAttrs |= 0x01;
    }
    return SyntheticMeta{createTime, modTime, mftModTime, readTime, fileSize, allocSize, fileAttrs, isDir};
}

// Write a resident StandardInformation attribute at `offset`; returns the offset
// just past it.
uint16_t WriteStandardInformationAttribute(uint8_t* record, uint16_t offset, const SyntheticMeta& meta) {
    auto* siAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    siAttr->TypeCode = StandardInformation;
    siAttr->FormCode = 0;
    siAttr->Form.Resident.ValueLength = 0x48;
    siAttr->Form.Resident.ValueOffset = 0x18;
    siAttr->RecordLength = (0x18 + 0x48 + 7) & ~7;

    auto* siValue = (record + offset + 0x18);
    memcpy(siValue + 0x00, &meta.createTime, 8);
    memcpy(siValue + 0x08, &meta.modTime, 8);
    memcpy(siValue + 0x10, &meta.mftModTime, 8);
    memcpy(siValue + 0x18, &meta.readTime, 8);
    memcpy(siValue + 0x20, &meta.fileAttrs, 4);

    return offset + static_cast<uint16_t>(siAttr->RecordLength);
}

// Write a resident FileName attribute at `offset`; returns the offset past it.
uint16_t WriteFileNameAttribute(uint8_t* record, uint16_t offset, const SyntheticRecordSpec& spec,
                                const SyntheticMeta& meta) {
    auto fnValueSize = static_cast<uint16_t>(sizeof(FILE_NAME) - sizeof(WCHAR) + (spec.nameLen * sizeof(WCHAR)));
    auto* fnAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    fnAttr->TypeCode = FileName;
    fnAttr->FormCode = 0;
    fnAttr->Form.Resident.ValueLength = fnValueSize;
    fnAttr->Form.Resident.ValueOffset = 0x18;
    fnAttr->RecordLength = (0x18 + fnValueSize + 7) & ~7;

    auto* nameAttr = reinterpret_cast<PFILE_NAME>(record + offset + 0x18);
    nameAttr->ParentDirectory.SegmentNumberLowPart = static_cast<ULONG>(spec.parentRecord & 0xFFFFFFFF);
    nameAttr->ParentDirectory.SegmentNumberHighPart = static_cast<USHORT>(spec.parentRecord >> 32);
    nameAttr->CreationTime = meta.createTime;
    nameAttr->ModificationTime = meta.modTime;
    nameAttr->MftModificationTime = meta.mftModTime;
    nameAttr->ReadTime = meta.readTime;
    nameAttr->AllocatedSize = meta.allocSize;
    nameAttr->FileSize = meta.fileSize;
    nameAttr->FileAttributes = meta.fileAttrs;
    nameAttr->FileNameLength = spec.nameLen;
    nameAttr->Flags = 3;
    StoreNtfsName(nameAttr->FileName, spec.nameLen, spec.name, spec.nameLen);

    return offset + static_cast<uint16_t>(fnAttr->RecordLength);
}

// Write a non-resident Data attribute (single mapping-pairs run) at `offset`;
// returns the offset past it.
uint16_t WriteDataAttribute(uint8_t* record, uint16_t offset, const SyntheticRecordSpec& spec,
                            const SyntheticMeta& meta) {
    auto* dataAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    dataAttr->TypeCode = Data;
    dataAttr->FormCode = 1;
    dataAttr->NameLength = 0;
    dataAttr->Form.Nonresident.LowestVcn.QuadPart = 0;
    dataAttr->Form.Nonresident.HighestVcn.QuadPart = static_cast<LONGLONG>((meta.allocSize / 4096) - 1);
    dataAttr->Form.Nonresident.MappingPairsOffset = 0x48;
    dataAttr->Form.Nonresident.AllocatedLength = static_cast<LONGLONG>(meta.allocSize);
    dataAttr->Form.Nonresident.FileSize = static_cast<LONGLONG>(meta.fileSize);
    dataAttr->Form.Nonresident.ValidDataLength = static_cast<LONGLONG>(meta.fileSize);
    uint64_t clusterCount = meta.allocSize / 4096;
    uint64_t clusterOffset = spec.recordIndex * 64;
    auto* runPtr = reinterpret_cast<uint8_t*>(dataAttr) + 0x48;
    *runPtr++ = 0x31;
    *runPtr++ = static_cast<uint8_t>(clusterCount & 0xFF);
    *runPtr++ = static_cast<uint8_t>(clusterOffset & 0xFF);
    *runPtr++ = static_cast<uint8_t>((clusterOffset >> 8) & 0xFF);
    *runPtr++ = static_cast<uint8_t>((clusterOffset >> 16) & 0xFF);
    *runPtr = 0;
    dataAttr->RecordLength = (0x48 + 6 + 7) & ~7;
    return offset + static_cast<uint16_t>(dataAttr->RecordLength);
}

void BuildSyntheticRecord(uint8_t* record, const SyntheticRecordSpec& spec, uint64_t* rng) {
    memset(record, 0, FILE_RECORD_SIZE);

    auto* hdr = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(record);
    hdr->MultiSectorHeader.Magic = 0x454C4946;
    hdr->MultiSectorHeader.UpdateSequenceArrayOffset = 0x30;
    hdr->MultiSectorHeader.UpdateSequenceArraySize = 3;
    hdr->SequenceNumber = static_cast<uint16_t>(spec.recordIndex + 1);
    hdr->Flags = spec.flags;
    hdr->FirstAttributeOffset = 0x38;

    hdr->BaseFileRecordSegment.SegmentNumberLowPart = static_cast<ULONG>(spec.baseRef & 0xFFFFFFFF);
    hdr->BaseFileRecordSegment.SegmentNumberHighPart = static_cast<USHORT>(spec.baseRef >> 32);

    if (spec.baseRef != 0 || ((spec.flags & 0x0001) == 0)) {
        auto* endAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + hdr->FirstAttributeOffset);
        endAttr->TypeCode = EndMarker;
        ApplyUSAProtection(record, static_cast<uint16_t>(spec.recordIndex & 0xFFFF));
        return;
    }

    SyntheticMeta meta = RollSyntheticMeta(spec.flags, rng);

    uint16_t offset = hdr->FirstAttributeOffset;
    offset = WriteStandardInformationAttribute(record, offset, meta);
    offset = WriteFileNameAttribute(record, offset, spec, meta);
    if (!meta.isDir && meta.fileSize > 0) {
        offset = WriteDataAttribute(record, offset, spec, meta);
    }

    auto* endAttr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    endAttr->TypeCode = EndMarker;

    ApplyUSAProtection(record, static_cast<uint16_t>(spec.recordIndex & 0xFFFF));
}

// Build one synthetic record at recordIndex into `record`. The local PRNG is
// seeded deterministically from recordIndex, so each record is reproducible and
// independent (workers need no shared state). The exact order of nextRng() calls
// is load-bearing - it determines record contents the parser-side tests assert on.
void BuildRecordForIndex(uint8_t* record, uint64_t recordIndex) {
    uint64_t rng = 12345678901ULL ^ ((recordIndex * 6364136223846793005ULL) + 1);
    auto nextRng = [&rng]() -> uint32_t {
        rng ^= rng << 13;
        rng ^= rng >> 7;
        rng ^= rng << 17;
        return static_cast<uint32_t>(rng & 0xFFFFFFFF);
    };

    uint32_t randomValue = nextRng();

    if (recordIndex < 5) {
        BuildSyntheticRecord(record, {recordIndex, 0, 0x0001, L"$MFT", 4, 0}, &rng);
    } else if (recordIndex == 5) {
        BuildSyntheticRecord(record, {recordIndex, 5, 0x0003, L".", 1, 0}, &rng);
    } else if (randomValue % 100 < 10) {
        memset(record, 0, FILE_RECORD_SIZE);
    } else if (randomValue % 100 < 25) {
        uint64_t baseRec = (nextRng() % recordIndex) + 1;
        BuildSyntheticRecord(record, {recordIndex, 0, 0x0001, L"ext", 3, baseRec}, &rng);
    } else if (randomValue % 100 < 40) {
        uint64_t parent = (recordIndex < 100) ? 5 : (nextRng() % (recordIndex / 2)) + 5;
        const wchar_t* name = dirNames[nextRng() % numDirNames];
        BuildSyntheticRecord(record, {recordIndex, parent, 0x0003, name, static_cast<uint8_t>(wcslen(name)), 0}, &rng);
    } else {
        uint64_t parent = (recordIndex < 100) ? 5 : (nextRng() % (recordIndex / 2)) + 5;
        const wchar_t* name = fileNames[nextRng() % numFileNames];
        BuildSyntheticRecord(record, {recordIndex, parent, 0x0001, name, static_cast<uint8_t>(wcslen(name)), 0}, &rng);
    }
}

void GenerateBatch(uint8_t* buffer, const BatchParams& params) {
    uint64_t perThread = (params.batchSize + params.threadCount - 1) / params.threadCount;
    std::vector<std::thread> workers;
    for (unsigned ti = 0; ti < params.threadCount; ti++) {
        uint64_t tStart = ti * perThread;
        uint64_t tEnd = tStart + perThread < params.batchSize ? tStart + perThread : params.batchSize;
        if (tStart >= params.batchSize) {
            break;
        }
        workers.emplace_back([buffer, tStart, tEnd, baseIndex = params.baseIndex]() {
            for (uint64_t i = tStart; i < tEnd; i++) {
                BuildRecordForIndex(buffer + (i * FILE_RECORD_SIZE), baseIndex + i);
            }
        });
    }
    for (auto& worker : workers) {
        worker.join();
    }
}

bool GenerateSyntheticMFTImpl(const char* filePath, RecordCount recordCount, uint32_t bufferSizeRecords) {
    auto* hFile = mftlib::platform::open_write(filePath);
    if (hFile == nullptr) {
        return false;
    }

    unsigned numThreads = EffectiveThreadCount();
    const size_t bufSize = static_cast<size_t>(bufferSizeRecords) * FILE_RECORD_SIZE;
    std::array<uint8_t*, 2> buf = {};
    buf[0] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    buf[1] = ShouldFailAlloc() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_alloc(bufSize));
    if ((buf[0] == nullptr) || (buf[1] == nullptr)) {
        if (buf[0] != nullptr) {
            mftlib::platform::big_free(buf[0], bufSize);
        }
        if (buf[1] != nullptr) {
            mftlib::platform::big_free(buf[1], bufSize);
        }
        mftlib::platform::close_file(hFile);
        return false;
    }

    uint64_t remaining = recordCount.value;
    uint64_t recordIndex = 0;
    int curBuf = 0;
    bool writeOk = true;
    std::thread writeThread;
    bool hasWritePending = false;
    int64_t writeOffset = 0;

    while (remaining > 0) {
        uint64_t batchSize =
            remaining < static_cast<uint64_t>(bufferSizeRecords) ? remaining : static_cast<uint64_t>(bufferSizeRecords);
        uint8_t* buffer = buf[curBuf];

        GenerateBatch(buffer, BatchParams{batchSize, recordIndex, numThreads});

        if (hasWritePending) {
            writeThread.join();
            hasWritePending = false;
            if (!writeOk) {
                break;
            }
        }

        uint64_t writeSize = batchSize;
        uint8_t* writeBuf = buffer;
        int64_t curOffset = writeOffset;
        writeThread = std::thread([hFile, writeBuf, writeSize, curOffset, &writeOk]() {
            auto byteCount = static_cast<size_t>(writeSize * FILE_RECORD_SIZE);
            int64_t written =
                mftlib::platform::pwrite_at(hFile, writeBuf, byteCount, mftlib::platform::FileOffset{curOffset});
            writeOk = (written == static_cast<int64_t>(byteCount));
        });
        hasWritePending = true;
        writeOffset += static_cast<int64_t>(batchSize * FILE_RECORD_SIZE);

        recordIndex += batchSize;
        remaining -= batchSize;
        curBuf = 1 - curBuf;
    }

    if (hasWritePending) {
        writeThread.join();
    }

    mftlib::platform::big_free(buf[0], bufSize);
    mftlib::platform::big_free(buf[1], bufSize);
    mftlib::platform::close_file(hFile);
    return writeOk;
}

}  // namespace

extern "C" {
#ifdef _WIN32
EXPORT bool GenerateSyntheticMFT(const wchar_t* filePath, uint64_t recordCount, uint32_t bufferSizeRecords) {
    if (ShouldFailPathConversion()) {
        return false;
    }
    int u8len = WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    if (u8len <= 0) {
        return false;
    }
    std::string utf8(static_cast<size_t>(u8len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8.data(), u8len, nullptr, nullptr);
    return GenerateSyntheticMFTImpl(utf8.c_str(), RecordCount{recordCount}, bufferSizeRecords);
}
#endif

#ifndef _WIN32
EXPORT bool GenerateSyntheticMFTUtf8(const char* filePath, uint64_t recordCount, uint32_t bufferSizeRecords) {
    return GenerateSyntheticMFTImpl(filePath, RecordCount{recordCount}, bufferSizeRecords);
}
#endif
}
