#include "pch.h"

#include <thread>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/ntfs_io.h"

static void ApplyUSAProtection(uint8_t* record, uint32_t recordSize, uint16_t usn) {
    auto* header = (PFILE_RECORD_SEGMENT_HEADER)record;
    uint16_t usaOffset = header->MultiSectorHeader.UpdateSequenceArrayOffset;
    uint16_t usaSize = header->MultiSectorHeader.UpdateSequenceArraySize;
    auto* usa = (uint16_t*)(record + usaOffset);
    usa[0] = usn;

    uint16_t sectorCount = usaSize - 1;
    for (uint16_t i = 0; i < sectorCount; i++) {
        uint32_t sectorEnd = (i + 1) * 512 - 2;
        if (sectorEnd + 2 > recordSize) break;
        auto* sectorLastWord = (uint16_t*)(record + sectorEnd);
        usa[i + 1] = *sectorLastWord;
        *sectorLastWord = usn;
    }
}

static void BuildSyntheticRecord(uint8_t* record, uint64_t recordIndex,
                                 uint64_t parentRecord, uint16_t flags,
                                 const wchar_t* name, uint8_t nameLen,
                                 uint64_t baseRef, uint64_t* rng) {
    memset(record, 0, FILE_RECORD_SIZE);

    auto nextRng = [rng]() -> uint64_t {
        *rng ^= *rng << 13;
        *rng ^= *rng >> 7;
        *rng ^= *rng << 17;
        return *rng;
    };

    auto* hdr = (PFILE_RECORD_SEGMENT_HEADER)record;
    hdr->MultiSectorHeader.Magic = 0x454C4946;
    hdr->MultiSectorHeader.UpdateSequenceArrayOffset = 0x30;
    hdr->MultiSectorHeader.UpdateSequenceArraySize = 3;
    hdr->SequenceNumber = (uint16_t)(recordIndex + 1);
    hdr->Flags = flags;
    hdr->FirstAttributeOffset = 0x38;

    hdr->BaseFileRecordSegment.SegmentNumberLowPart = (ULONG)(baseRef & 0xFFFFFFFF);
    hdr->BaseFileRecordSegment.SegmentNumberHighPart = (USHORT)(baseRef >> 32);

    if (baseRef != 0 || !(flags & 0x0001)) {
        auto* endAttr = (PATTRIBUTE_RECORD_HEADER)(record + hdr->FirstAttributeOffset);
        endAttr->TypeCode = EndMarker;
        ApplyUSAProtection(record, FILE_RECORD_SIZE, (uint16_t)(recordIndex & 0xFFFF));
        return;
    }

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
    if (nextRng() % 20 == 0) fileAttrs |= 0x02;
    if (nextRng() % 50 == 0) fileAttrs |= 0x04;
    if (nextRng() % 10 == 0) fileAttrs |= 0x01;

    uint16_t offset = hdr->FirstAttributeOffset;

    auto* siAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
    siAttr->TypeCode = StandardInformation;
    siAttr->FormCode = 0;
    siAttr->Form.Resident.ValueLength = 0x48;
    siAttr->Form.Resident.ValueOffset = 0x18;
    siAttr->RecordLength = (0x18 + 0x48 + 7) & ~7;

    auto* siValue = (uint8_t*)(record + offset + 0x18);
    memcpy(siValue + 0x00, &createTime, 8);
    memcpy(siValue + 0x08, &modTime, 8);
    memcpy(siValue + 0x10, &mftModTime, 8);
    memcpy(siValue + 0x18, &readTime, 8);
    memcpy(siValue + 0x20, &fileAttrs, 4);

    offset += (uint16_t)siAttr->RecordLength;

    uint16_t fnValueSize = (uint16_t)(sizeof(FILE_NAME) - sizeof(WCHAR) + nameLen * sizeof(WCHAR));
    auto* fnAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
    fnAttr->TypeCode = FileName;
    fnAttr->FormCode = 0;
    fnAttr->Form.Resident.ValueLength = fnValueSize;
    fnAttr->Form.Resident.ValueOffset = 0x18;
    fnAttr->RecordLength = (0x18 + fnValueSize + 7) & ~7;

    auto* fn = (PFILE_NAME)(record + offset + 0x18);
    fn->ParentDirectory.SegmentNumberLowPart = (ULONG)(parentRecord & 0xFFFFFFFF);
    fn->ParentDirectory.SegmentNumberHighPart = (USHORT)(parentRecord >> 32);
    fn->CreationTime = createTime;
    fn->ModificationTime = modTime;
    fn->MftModificationTime = mftModTime;
    fn->ReadTime = readTime;
    fn->AllocatedSize = allocSize;
    fn->FileSize = fileSize;
    fn->FileAttributes = fileAttrs;
    fn->FileNameLength = nameLen;
    fn->Flags = 3;
    wmemcpy(fn->FileName, name, nameLen);

    offset += (uint16_t)fnAttr->RecordLength;

    if (!isDir && fileSize > 0) {
        auto* dataAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
        dataAttr->TypeCode = Data;
        dataAttr->FormCode = 1;
        dataAttr->NameLength = 0;
        dataAttr->Form.Nonresident.LowestVcn.QuadPart = 0;
        dataAttr->Form.Nonresident.HighestVcn.QuadPart = (allocSize / 4096) - 1;
        dataAttr->Form.Nonresident.MappingPairsOffset = 0x48;
        dataAttr->Form.Nonresident.AllocatedLength = allocSize;
        dataAttr->Form.Nonresident.FileSize = fileSize;
        dataAttr->Form.Nonresident.ValidDataLength = fileSize;
        uint64_t clusterCount = allocSize / 4096;
        uint64_t clusterOffset = recordIndex * 64;
        auto* runPtr = (uint8_t*)dataAttr + 0x48;
        *runPtr++ = 0x31;
        *runPtr++ = (uint8_t)(clusterCount & 0xFF);
        *runPtr++ = (uint8_t)(clusterOffset & 0xFF);
        *runPtr++ = (uint8_t)((clusterOffset >> 8) & 0xFF);
        *runPtr++ = (uint8_t)((clusterOffset >> 16) & 0xFF);
        *runPtr++ = 0;
        dataAttr->RecordLength = (0x48 + 6 + 7) & ~7;
        offset += (uint16_t)dataAttr->RecordLength;
    }

    auto* endAttr = (PATTRIBUTE_RECORD_HEADER)(record + offset);
    endAttr->TypeCode = EndMarker;

    ApplyUSAProtection(record, FILE_RECORD_SIZE, (uint16_t)(recordIndex & 0xFFFF));
}

extern "C" {
    EXPORT bool GenerateSyntheticMFT(const wchar_t* filePath, uint64_t recordCount, uint32_t bufferSizeRecords) {
        HANDLE hFile = CreateFileW(filePath, GENERIC_WRITE, 0, nullptr,
                                   CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) return false;

        unsigned numThreads = EffectiveThreadCount();

        const wchar_t* fileNames[] = {
            L"README.md", L"index.html", L"main.cpp", L"package.json",
            L"Makefile", L"config.yaml", L"data.bin", L"icon.png",
            L"setup.py", L"app.js", L"style.css", L"test.go",
            L"build.gradle", L"Cargo.toml", L"Program.cs", L"pom.xml",
        };
        const wchar_t* dirNames[] = {
            L"src", L"bin", L"obj", L"node_modules", L".git",
            L"build", L"docs", L"tests", L"lib", L"include",
            L"assets", L"scripts", L"config", L"data", L"temp", L"cache",
        };
        constexpr int numFileNames = 16;
        constexpr int numDirNames = 16;

        const size_t bufSize = (size_t)bufferSizeRecords * FILE_RECORD_SIZE;
        uint8_t* buf[2];
        buf[0] = ShouldFailAlloc() ? nullptr : (uint8_t*)VirtualAlloc(nullptr, bufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        buf[1] = ShouldFailAlloc() ? nullptr : (uint8_t*)VirtualAlloc(nullptr, bufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!buf[0] || !buf[1]) {
            if (buf[0]) VirtualFree(buf[0], 0, MEM_RELEASE);
            if (buf[1]) VirtualFree(buf[1], 0, MEM_RELEASE);
            CloseHandle(hFile);
            return false;
        }

        uint64_t remaining = recordCount;
        uint64_t recordIndex = 0;
        int curBuf = 0;
        bool writeOk = true;
        std::thread writeThread;
        bool hasWritePending = false;

        while (remaining > 0) {
            uint64_t batchSize = min(remaining, (uint64_t)bufferSizeRecords);
            uint8_t* buffer = buf[curBuf];

            uint64_t perThread = (batchSize + numThreads - 1) / numThreads;
            std::vector<std::thread> workers;
            for (unsigned t = 0; t < numThreads; t++) {
                uint64_t tStart = t * perThread;
                uint64_t tEnd = min(tStart + perThread, batchSize);
                if (tStart >= batchSize) break;
                uint64_t baseIdx = recordIndex;

                workers.emplace_back([buffer, tStart, tEnd, baseIdx,
                                      &fileNames, &dirNames, numFileNames, numDirNames]() {
                    for (uint64_t i = tStart; i < tEnd; i++) {
                        uint64_t ri = baseIdx + i;
                        uint8_t* record = buffer + i * FILE_RECORD_SIZE;

                        uint64_t rng = 12345678901ULL ^ (ri * 6364136223846793005ULL + 1);
                        auto nextRng = [&rng]() -> uint32_t {
                            rng ^= rng << 13;
                            rng ^= rng >> 7;
                            rng ^= rng << 17;
                            return (uint32_t)(rng & 0xFFFFFFFF);
                        };

                        uint32_t r = nextRng();

                        if (ri < 5) {
                            BuildSyntheticRecord(record, ri, 0, 0x0001, L"$MFT", 4, 0, &rng);
                        } else if (ri == 5) {
                            BuildSyntheticRecord(record, ri, 5, 0x0003, L".", 1, 0, &rng);
                        } else if (r % 100 < 10) {
                            memset(record, 0, FILE_RECORD_SIZE);
                        } else if (r % 100 < 25) {
                            uint64_t baseRec = (nextRng() % ri) + 1;
                            BuildSyntheticRecord(record, ri, 0, 0x0001, L"ext", 3, baseRec, &rng);
                        } else if (r % 100 < 40) {
                            uint64_t parent = (ri < 100) ? 5 : (nextRng() % (ri / 2)) + 5;
                            const wchar_t* name = dirNames[nextRng() % numDirNames];
                            BuildSyntheticRecord(record, ri, parent, 0x0003, name, (uint8_t)wcslen(name), 0, &rng);
                        } else {
                            uint64_t parent = (ri < 100) ? 5 : (nextRng() % (ri / 2)) + 5;
                            const wchar_t* name = fileNames[nextRng() % numFileNames];
                            BuildSyntheticRecord(record, ri, parent, 0x0001, name, (uint8_t)wcslen(name), 0, &rng);
                        }
                    }
                });
            }
            for (auto& w : workers) w.join();

            if (hasWritePending) {
                writeThread.join();
                if (!writeOk) break;
            }

            uint64_t writeSize = batchSize;
            uint8_t* writeBuf = buffer;
            writeThread = std::thread([hFile, writeBuf, writeSize, &writeOk]() {
                DWORD written;
                writeOk = WriteFile(hFile, writeBuf, (DWORD)(writeSize * FILE_RECORD_SIZE), &written, nullptr);
            });
            hasWritePending = true;

            recordIndex += batchSize;
            remaining -= batchSize;
            curBuf = 1 - curBuf;
        }

        if (hasWritePending) writeThread.join();

        VirtualFree(buf[0], 0, MEM_RELEASE);
        VirtualFree(buf[1], 0, MEM_RELEASE);
        CloseHandle(hFile);
        return writeOk;
    }
}
