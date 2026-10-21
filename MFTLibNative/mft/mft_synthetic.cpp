#include "pch.h"

#include <array>
// ReSharper disable once CppUnusedIncludeDirective
// aislop-ignore-next-line CppUnusedIncludeDirective -- memcpy/memset below need this on GCC/Clang
#include <cstring>
#include <thread>

#include "../framework.h"
#include "../ntfs.h"
#include "../internal.h"
#include "../core/platform.h"
#include "mft.internal.h"
// ReSharper disable once CppUnusedIncludeDirective
// aislop-ignore-next-line CppUnusedIncludeDirective -- constants consumed by the included fixture fragment
#include "mft_fixture.h"

namespace {
constexpr std::array<const wchar_t*, 16> fileNames = {
    L"README.md",    L"index.html", L"main.cpp",   L"package.json", L"Makefile",  L"config.yaml",
    L"data.bin",     L"icon.png",   L"setup.py",   L"app.js",       L"style.css", L"test.go",
    L"build.gradle", L"Cargo.toml", L"Program.cs", L"pom.xml",
};
constexpr std::array<const wchar_t*, 16> directoryNames = {
    L"src", L"bin",     L"obj",    L"node_modules", L".git",   L"build", L"docs", L"tests",
    L"lib", L"include", L"assets", L"scripts",      L"config", L"data",  L"temp", L"cache",
};
constexpr int fileNameCount = 16;
constexpr int directoryNameCount = 16;

// Descriptor for one synthetic record, bundled so the several u64/u16 fields
// cannot be transposed at the call site.
struct SyntheticRecordSpec {
    uint64_t recordIndex = 0;
    uint64_t parentRecord = 0;
    uint16_t flags = 0;
    const wchar_t* name = nullptr;
    uint8_t nameLength = 0;
    uint64_t baseReference = 0;
    uint32_t recordSize = 0;
    uint16_t parentSequenceNumber = 0;
};

// Work assignment for one buffer batch.
struct BatchParameters {
    uint64_t batchSize = 0;
    uint64_t baseIndex = 0;
    unsigned threadCount = 0;
    uint32_t recordSize = 0;
};

// Strong type for a total record count, kept distinct from the per-buffer size.
struct RecordCount {
    uint64_t value;
};

void ApplyUSAProtection(uint8_t* record, ParseGeometry geometry, uint16_t usn) {
    auto* header = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(record);
    uint16_t usaOffset = header->MultiSectorHeader.UpdateSequenceArrayOffset;
    uint16_t usaSize = header->MultiSectorHeader.UpdateSequenceArraySize;
    auto* usa = reinterpret_cast<uint16_t*>(record + usaOffset);
    usa[0] = usn;

    uint16_t sectorCount = usaSize - 1;
    for (uint16_t i = 0; i < sectorCount; i++) {
        uint32_t sectorEnd = ((i + 1) * 512) - 2;
        if (sectorEnd + 2 > geometry.recordSize) {
            break;
        }
        auto* sectorLastWord = reinterpret_cast<uint16_t*>(record + sectorEnd);
        usa[i + 1] = *sectorLastWord;
        *sectorLastWord = usn;
    }
}

// Narrow a wchar_t literal/string to NTFS WCHAR (UTF-16, char16_t units).
// On Windows wchar_t is 16-bit so this is a direct copy.
// On Linux wchar_t is 32-bit; we truncate each unit to 16 bits.
void StoreNtfsName(WCHAR* destination, uint16_t destinationCapacity, const wchar_t* source, uint16_t sourceLength) {
    uint16_t copyLength = sourceLength < destinationCapacity ? sourceLength : destinationCapacity;
    for (uint16_t i = 0; i < copyLength; i++) {
        auto unit = static_cast<uint16_t>(static_cast<uint32_t>(source[i]) & 0xFFFFU);
        memcpy(destination + i, &unit, sizeof(uint16_t));
    }
}

// Timestamps, sizes, and attribute flags for one synthetic record, rolled from
// the per-record PRNG. The exact order of nextRandom() calls is load-bearing: it
// determines record contents the parser-side tests assert on.
struct SyntheticMeta {
    uint64_t createTime;
    uint64_t modTime;
    uint64_t mftModTime;
    uint64_t readTime;
    uint64_t fileSize;
    uint64_t allocatedSize;
    uint32_t fileAttributes;
    bool isDirectory;
};

SyntheticMeta RollSyntheticMeta(uint16_t flags, uint64_t* randomState) {
    auto nextRandom = [randomState]() -> uint64_t {
        *randomState ^= *randomState << 13;
        *randomState ^= *randomState >> 7;
        *randomState ^= *randomState << 17;
        return *randomState;
    };

    constexpr uint64_t ftBase2015 = 130645440000000000ULL;
    constexpr uint64_t tenYearsTicks = 10ULL * 365 * 24 * 3600 * 10000000ULL;
    uint64_t createTime = ftBase2015 + (nextRandom() % tenYearsTicks);
    uint64_t modTime = createTime + (nextRandom() % (tenYearsTicks / 10));
    uint64_t mftModTime = modTime + (nextRandom() % 100000000ULL);
    uint64_t readTime = modTime + (nextRandom() % (tenYearsTicks / 5));

    bool isDirectory = (flags & kRecordDirectory) != 0;
    uint64_t fileSize = isDirectory ? 0 : (nextRandom() % (256ULL * 1024 * 1024));
    uint64_t allocatedSize = (fileSize + 4095) & ~4095ULL;
    uint32_t fileAttributes = isDirectory ? 0x10 : 0x20;
    if (nextRandom() % 20 == 0) {
        fileAttributes |= 0x02;
    }
    if (nextRandom() % 50 == 0) {
        fileAttributes |= 0x04;
    }
    if (nextRandom() % 10 == 0) {
        fileAttributes |= 0x01;
    }
    return SyntheticMeta{createTime, modTime,       mftModTime,     readTime,
                         fileSize,   allocatedSize, fileAttributes, isDirectory};
}

// Write a resident StandardInformation attribute at `offset`; returns the offset
// just past it.
uint16_t WriteStandardInformationAttribute(uint8_t* record, uint16_t offset, const SyntheticMeta& meta) {
    auto* standardInformationAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    standardInformationAttribute->TypeCode = StandardInformation;
    standardInformationAttribute->FormCode = 0;
    standardInformationAttribute->Form.Resident.ValueLength = 0x48;
    standardInformationAttribute->Form.Resident.ValueOffset = 0x18;
    standardInformationAttribute->RecordLength = (0x18 + 0x48 + 7) & ~7;

    auto* standardInformationValue = (record + offset + 0x18);
    memcpy(standardInformationValue + 0x00, &meta.createTime, 8);
    memcpy(standardInformationValue + 0x08, &meta.modTime, 8);
    memcpy(standardInformationValue + 0x10, &meta.mftModTime, 8);
    memcpy(standardInformationValue + 0x18, &meta.readTime, 8);
    memcpy(standardInformationValue + 0x20, &meta.fileAttributes, 4);

    return offset + static_cast<uint16_t>(standardInformationAttribute->RecordLength);
}

// Write a resident FileName attribute at `offset`; returns the offset past it.
uint16_t WriteFileNameAttribute(uint8_t* record, uint16_t offset, const SyntheticRecordSpec& spec,
                                const SyntheticMeta& meta) {
    auto fileNameValueSize =
        static_cast<uint16_t>(sizeof(FILE_NAME) - sizeof(WCHAR) + (spec.nameLength * sizeof(WCHAR)));
    auto* fileNameAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    fileNameAttribute->TypeCode = FileName;
    fileNameAttribute->FormCode = 0;
    fileNameAttribute->Form.Resident.ValueLength = fileNameValueSize;
    fileNameAttribute->Form.Resident.ValueOffset = 0x18;
    fileNameAttribute->RecordLength = (0x18 + fileNameValueSize + 7) & ~7;

    auto* nameAttribute = reinterpret_cast<PFILE_NAME>(record + offset + 0x18);
    nameAttribute->ParentDirectory.SegmentNumberLowPart = static_cast<ULONG>(spec.parentRecord & 0xFFFFFFFF);
    nameAttribute->ParentDirectory.SegmentNumberHighPart = static_cast<USHORT>(spec.parentRecord >> 32);
    nameAttribute->ParentDirectory.SequenceNumber = spec.parentSequenceNumber;
    nameAttribute->CreationTime = meta.createTime;
    nameAttribute->ModificationTime = meta.modTime;
    nameAttribute->MftModificationTime = meta.mftModTime;
    nameAttribute->ReadTime = meta.readTime;
    nameAttribute->AllocatedSize = meta.allocatedSize;
    nameAttribute->FileSize = meta.fileSize;
    nameAttribute->FileAttributes = meta.fileAttributes;
    nameAttribute->FileNameLength = spec.nameLength;
    nameAttribute->Flags = 3;
    StoreNtfsName(nameAttribute->FileName, spec.nameLength, spec.name, spec.nameLength);

    return offset + static_cast<uint16_t>(fileNameAttribute->RecordLength);
}

// Write a non-resident Data attribute (single mapping-pairs run) at `offset`;
// returns the offset past it.
uint16_t WriteDataAttribute(uint8_t* record, uint16_t offset, const SyntheticRecordSpec& spec,
                            const SyntheticMeta& meta) {
    auto* dataAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    dataAttribute->TypeCode = Data;
    dataAttribute->FormCode = 1;
    dataAttribute->NameLength = 0;
    dataAttribute->Form.Nonresident.LowestVcn.QuadPart = 0;
    dataAttribute->Form.Nonresident.HighestVcn.QuadPart = static_cast<LONGLONG>((meta.allocatedSize / 4096) - 1);
    dataAttribute->Form.Nonresident.MappingPairsOffset = 0x48;
    dataAttribute->Form.Nonresident.AllocatedLength = static_cast<LONGLONG>(meta.allocatedSize);
    dataAttribute->Form.Nonresident.FileSize = static_cast<LONGLONG>(meta.fileSize);
    dataAttribute->Form.Nonresident.ValidDataLength = static_cast<LONGLONG>(meta.fileSize);
    uint64_t clusterCount = meta.allocatedSize / 4096;
    uint64_t clusterOffset = spec.recordIndex * 64;
    auto* runPointer = reinterpret_cast<uint8_t*>(dataAttribute) + 0x48;
    *runPointer++ = 0x31;
    *runPointer++ = static_cast<uint8_t>(clusterCount & 0xFF);
    *runPointer++ = static_cast<uint8_t>(clusterOffset & 0xFF);
    *runPointer++ = static_cast<uint8_t>((clusterOffset >> 8) & 0xFF);
    *runPointer++ = static_cast<uint8_t>((clusterOffset >> 16) & 0xFF);
    *runPointer = 0;
    dataAttribute->RecordLength = (0x48 + 6 + 7) & ~7;
    return offset + static_cast<uint16_t>(dataAttribute->RecordLength);
}

void BuildSyntheticRecord(uint8_t* record, const SyntheticRecordSpec& spec, uint64_t* randomState) {
    memset(record, 0, spec.recordSize);

    auto* recordHeader = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(record);
    recordHeader->MultiSectorHeader.Magic = kFileRecordMagic;
    recordHeader->MultiSectorHeader.UpdateSequenceArrayOffset = 0x30;
    auto usaSize = static_cast<uint16_t>((spec.recordSize / 512U) + 1U);
    recordHeader->MultiSectorHeader.UpdateSequenceArraySize = usaSize;
    recordHeader->SequenceNumber = static_cast<uint16_t>(spec.recordIndex + 1);
    recordHeader->Flags = spec.flags;
    recordHeader->FirstAttributeOffset = static_cast<uint16_t>((0x30 + (usaSize * sizeof(uint16_t)) + 7) & ~7);
    memcpy(record + 0x1C, &spec.recordSize, sizeof(spec.recordSize));

    recordHeader->BaseFileRecordSegment.SegmentNumberLowPart = static_cast<ULONG>(spec.baseReference & 0xFFFFFFFF);
    recordHeader->BaseFileRecordSegment.SegmentNumberHighPart = static_cast<USHORT>(spec.baseReference >> 32);

    if (spec.baseReference != 0 || ((spec.flags & kRecordInUse) == 0)) {
        auto* endAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + recordHeader->FirstAttributeOffset);
        endAttribute->TypeCode = EndMarker;
        ApplyUSAProtection(record, ParseGeometry{spec.recordSize}, static_cast<uint16_t>(spec.recordIndex & 0xFFFF));
        return;
    }

    SyntheticMeta meta = RollSyntheticMeta(spec.flags, randomState);

    uint16_t offset = recordHeader->FirstAttributeOffset;
    offset = WriteStandardInformationAttribute(record, offset, meta);
    offset = WriteFileNameAttribute(record, offset, spec, meta);
    if (!meta.isDirectory && meta.fileSize > 0) {
        offset = WriteDataAttribute(record, offset, spec, meta);
    }

    auto* endAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    endAttribute->TypeCode = EndMarker;

    ApplyUSAProtection(record, ParseGeometry{spec.recordSize}, static_cast<uint16_t>(spec.recordIndex & 0xFFFF));
}

// Build one synthetic record at recordIndex into `record`. The local PRNG is
// seeded deterministically from recordIndex, so each record is reproducible and
// independent (workers need no shared state). The exact order of nextRandom() calls
// is load-bearing - it determines record contents the parser-side tests assert on.
void BuildRecordForIndex(uint8_t* record, uint32_t recordSize, uint64_t recordIndex) {
    uint64_t randomState = 12345678901ULL ^ ((recordIndex * 6364136223846793005ULL) + 1);
    auto nextRandom = [&randomState]() -> uint32_t {
        randomState ^= randomState << 13;
        randomState ^= randomState >> 7;
        randomState ^= randomState << 17;
        return static_cast<uint32_t>(randomState & 0xFFFFFFFF);
    };

    uint32_t randomValue = nextRandom();

    if (recordIndex < 5) {
        BuildSyntheticRecord(record, {recordIndex, 0, kRecordInUse, L"$MFT", 4, 0, recordSize}, &randomState);
    } else if (recordIndex == 5) {
        BuildSyntheticRecord(record, {recordIndex, 5, kRecordInUse | kRecordDirectory, L".", 1, 0, recordSize},
                             &randomState);
    } else if (randomValue % 100 < 10) {
        memset(record, 0, recordSize);
    } else if (randomValue % 100 < 25) {
        uint64_t baseRecordNumber = (nextRandom() % recordIndex) + 1;
        BuildSyntheticRecord(record, {recordIndex, 0, kRecordInUse, L"ext", 3, baseRecordNumber, recordSize},
                             &randomState);
    } else if (randomValue % 100 < 40) {
        uint64_t parent = (recordIndex < 100) ? 5 : (nextRandom() % (recordIndex / 2)) + 5;
        const wchar_t* name = directoryNames[nextRandom() % directoryNameCount];
        BuildSyntheticRecord(record,
                             {recordIndex, parent, kRecordInUse | kRecordDirectory, name,
                              static_cast<uint8_t>(wcslen(name)), 0, recordSize},
                             &randomState);
    } else {
        uint64_t parent = (recordIndex < 100) ? 5 : (nextRandom() % (recordIndex / 2)) + 5;
        const wchar_t* name = fileNames[nextRandom() % fileNameCount];
        BuildSyntheticRecord(
            record, {recordIndex, parent, kRecordInUse, name, static_cast<uint8_t>(wcslen(name)), 0, recordSize},
            &randomState);
    }
}

void GenerateBatch(uint8_t* buffer, const BatchParameters& parameters) {
    ForEachRange(parameters.batchSize, parameters.threadCount, [&](unsigned, SliceRange range) {
        for (uint64_t i = range.start; i < range.end; i++) {
            BuildRecordForIndex(buffer + (static_cast<size_t>(parameters.recordSize) * i), parameters.recordSize,
                                parameters.baseIndex + i);
        }
    });
}

bool GenerateSyntheticMFTImpl(const char* filePath, RecordCount recordCount, uint32_t bufferSizeRecords,
                              uint32_t recordSize) {
    if (!IsSupportedRecordSize(recordSize) || bufferSizeRecords == 0) {
        return false;
    }
    auto* fileHandle = mftlib::platform::open_write(filePath);
    if (fileHandle == nullptr) {
        return false;
    }
    const NativeScopeExit opened([&]() { mftlib::platform::close_file(fileHandle); });

    unsigned threadCount = EffectiveThreadCount(nullptr);
    const size_t bufferSize = static_cast<size_t>(bufferSizeRecords) * recordSize;
    std::array<uint8_t*, 2> chunkBuffers = {};
    const NativeScopeExit buffers([&]() {
        mftlib::platform::big_free(chunkBuffers[0], bufferSize);
        mftlib::platform::big_free(chunkBuffers[1], bufferSize);
    });
    chunkBuffers[0] =
        ShouldFailAllocation() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_allocate(bufferSize));
    chunkBuffers[1] =
        ShouldFailAllocation() ? nullptr : static_cast<uint8_t*>(mftlib::platform::big_allocate(bufferSize));
    if ((chunkBuffers[0] == nullptr) || (chunkBuffers[1] == nullptr)) {
        return false;
    }

    uint64_t remaining = recordCount.value;
    uint64_t recordIndex = 0;
    int currentBufferIndex = 0;
    bool writeOk = true;
    std::thread writeThread;
    NativeFailure writeFailure = NativeFailure::None;
    const NativeScopeExit joined([&]() {
        if (writeThread.joinable()) {
            writeThread.join();
        }
    });
    bool hasWritePending = false;
    int64_t writeOffset = 0;

    while (remaining > 0) {
        uint64_t batchSize =
            remaining < static_cast<uint64_t>(bufferSizeRecords) ? remaining : static_cast<uint64_t>(bufferSizeRecords);
        uint8_t* buffer = chunkBuffers[currentBufferIndex];

        GenerateBatch(buffer, BatchParameters{batchSize, recordIndex, threadCount, recordSize});

        if (hasWritePending) {
            writeThread.join();
            ThrowNativeFailure(writeFailure);
            hasWritePending = false;
            if (!writeOk) {
                break;
            }
        }

        uint64_t writeSize = batchSize;
        uint8_t* writeBuffer = buffer;
        int64_t currentOffset = writeOffset;
        writeThread = StartNativeThread(
            [fileHandle, writeBuffer, writeSize, currentOffset, recordSize, &writeOk]() {
                auto byteCount = static_cast<size_t>(writeSize * recordSize);
                int64_t written = mftlib::platform::pwrite_at(fileHandle, writeBuffer, byteCount,
                                                              mftlib::platform::FileOffset{currentOffset});
                writeOk = (written == static_cast<int64_t>(byteCount));
            },
            writeFailure);
        hasWritePending = true;
        writeOffset += static_cast<int64_t>(batchSize * recordSize);

        recordIndex += batchSize;
        remaining -= batchSize;
        currentBufferIndex = 1 - currentBufferIndex;
    }

    if (hasWritePending) {
        writeThread.join();
    }
    ThrowNativeFailure(writeFailure);

    return writeOk;
}

#define AISLOP_TU_FRAGMENT_SYNTHETIC
// NOLINTNEXTLINE(bugprone-suspicious-include) -- component-as-TU pattern, see mft.cpp
#include "mft_synthetic.fixture.cpp"
#undef AISLOP_TU_FRAGMENT_SYNTHETIC
}  // namespace

extern "C" {
// Path is UTF-8 on every platform.
EXPORT bool GenerateSyntheticMFTSizedUtf8(const char* filePath, uint64_t recordCount, uint32_t bufferSizeRecords,
                                          uint32_t recordSize) {
    return NativeBoundary(
        [&]() { return GenerateSyntheticMFTImpl(filePath, RecordCount{recordCount}, bufferSizeRecords, recordSize); },
        [](const wchar_t*) { return false; });
}

// Path is UTF-8 on every platform.
EXPORT bool GenerateFixtureMFTUtf8(const char* filePath) {
    return NativeBoundary([&]() { return GenerateFixtureMFTImpl(filePath); }, [](const wchar_t*) { return false; });
}
}
