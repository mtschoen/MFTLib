// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.records.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <array>
#include <cstdint>
#include <cstdlib>
#include <cstring>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "mft.internal.h"

namespace {

bool FileNameMatches(const WCHAR* name, uint8_t nameLen, const FilterSpec& filter) {
#ifdef _WIN32
    if ((filter.flags & 1) != 0U) {
        if (nameLen != filter.length) {
            return false;
        }
        return _wcsnicmp(name, filter.text, nameLen) == 0;
    }
    if ((filter.flags & 2) != 0U) {
        if (filter.length > nameLen) {
            return false;
        }
        for (uint16_t i = 0; i <= nameLen - filter.length; i++) {
            if (_wcsnicmp(name + i, filter.text, filter.length) == 0) {
                return true;
            }
        }
        return false;
    }
#else
    (void)name;
    (void)nameLen;
    (void)filter;
#endif
    return false;
}

// Copy NTFS WCHAR units (UTF-16, char16_t) to wchar_t, combining surrogate pairs on Linux.
// Returns the number of wchar_t codepoints written.
uint16_t CopyNtfsNameUnits(wchar_t* dst, uint16_t dstCapacity, const WCHAR* src, uint16_t srcLen) {
    uint16_t out = 0;
    for (uint16_t i = 0; i < srcLen && out < dstCapacity; i++) {
        uint16_t unit;
        memcpy(&unit, src + i, sizeof(WCHAR));
#ifndef _WIN32
        if (unit >= 0xD800 && unit <= 0xDBFF && i + 1 < srcLen) {
            uint16_t low;
            memcpy(&low, src + i + 1, sizeof(WCHAR));
            if (low >= 0xDC00 && low <= 0xDFFF) {
                uint32_t codepoint = 0x10000u + (static_cast<uint32_t>(unit - 0xD800) << 10) + (low - 0xDC00);
                dst[out++] = static_cast<wchar_t>(codepoint);
                i++;
                continue;
            }
        }
#endif
        dst[out++] = static_cast<wchar_t>(unit);
    }
    return out;
}

// Copy a NTFS WCHAR name (UTF-16, char16_t units) into a wchar_t buffer.
// On Windows (wchar_t==uint16_t) this is a direct unit copy.
// On Linux (wchar_t==uint32_t) surrogate pairs are combined into a single codepoint.
// Always null-terminates if there's room (callers pass dstCapacity > srcLen in practice).
void CopyNtfsName(wchar_t* dst, uint16_t dstCapacity, const WCHAR* src, uint16_t srcLen) {
    uint16_t out = CopyNtfsNameUnits(dst, dstCapacity, src, srcLen);
    if (out < dstCapacity) {
        dst[out] = L'\0';
    }
}

// Walk a record's attributes, accumulating the StandardInformation file
// attributes into *siAttributes (*sawStandardInformation set if seen), and stop
// at the first resident, non-DOS FileName attribute. Returns that FileName
// attribute, or nullptr if the record has none.
PFILE_NAME FindNamedAttribute(PFILE_RECORD_SEGMENT_HEADER rec, uint32_t* siAttributes, bool* sawStandardInformation) {
    *siAttributes = 0;
    *sawStandardInformation = false;
    auto* attr =
        reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(rec) + rec->FirstAttributeOffset);
    while (reinterpret_cast<uint8_t*>(attr) - reinterpret_cast<uint8_t*>(rec) < FILE_RECORD_SIZE) {
        if (attr->TypeCode == EndMarker || attr->RecordLength == 0) {
            break;
        }
        if (attr->TypeCode == StandardInformation && attr->FormCode == 0) {
            auto* siValue = reinterpret_cast<uint8_t*>(attr) + attr->Form.Resident.ValueOffset;
            *siAttributes = *reinterpret_cast<uint32_t*>(siValue + 32);
            *sawStandardInformation = true;
        } else if (attr->TypeCode == FileName && attr->FormCode == 0) {
            auto* nameAttr =
                reinterpret_cast<PFILE_NAME>(reinterpret_cast<uint8_t*>(attr) + attr->Form.Resident.ValueOffset);
            if (nameAttr->Flags != 2) {
                return nameAttr;
            }
        }
        attr = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(attr) + attr->RecordLength);
    }
    return nullptr;
}

// Scan one file record. If it is an in-use, non-extension record with a non-DOS
// FileName that passes the filter, fill *outEntry and return true. Side effect:
// stores the name into the path-lookup table when one is provided. Shared by
// ProcessRecordSlice (worker threads) and ProcessRecordBatch.
bool ScanRecordForEntry(uint8_t* recPtr, uint64_t recordIndex, const ScanContext& scan, MftFileEntry* outEntry) {
    auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);

    if (rec->MultiSectorHeader.Magic != 0x454C4946) {
        return false;
    }
    if ((rec->Flags & 0x0001) == 0) {
        return false;
    }

    uint64_t baseRef = static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberLowPart) |
                       (static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberHighPart) << 32);
    if (baseRef != 0) {
        return false;
    }

    uint32_t siAttributes = 0;
    bool sawStandardInformation = false;
    auto* nameAttr = FindNamedAttribute(rec, &siAttributes, &sawStandardInformation);
    if (nameAttr == nullptr) {
        return false;
    }

    uint64_t parent = static_cast<uint64_t>(nameAttr->ParentDirectory.SegmentNumberLowPart) |
                      (static_cast<uint64_t>(nameAttr->ParentDirectory.SegmentNumberHighPart) << 32);

    if ((scan.lookup != nullptr) && recordIndex < scan.totalRecords) {
        scan.lookup->storeName(recordIndex, parent, nameAttr->FileName, nameAttr->FileNameLength);
    }

    if ((scan.filter.text != nullptr) && !FileNameMatches(nameAttr->FileName, nameAttr->FileNameLength, scan.filter)) {
        return false;
    }

    memset(outEntry, 0, sizeof(MftFileEntry));
    outEntry->recordNumber = recordIndex;
    outEntry->parentRecordNumber = parent;
    outEntry->flags = rec->Flags;
    outEntry->fileNameLength = nameAttr->FileNameLength;
    outEntry->fileAttributes = sawStandardInformation ? siAttributes : nameAttr->FileAttributes;
    // FileNameLength is UCHAR (max 255) which fits in fileName[260]; no clamping needed.
    CopyNtfsName(outEntry->fileName, 260, nameAttr->FileName, nameAttr->FileNameLength);
    return true;
}

}  // namespace

uint16_t ResolvePath(uint64_t recordIndex, const PathLookup& lookup, uint64_t totalRecords, wchar_t* pathBuf,
                     uint16_t pathBufSize) {
    struct Component {
        const uint8_t* nameBytes;
        uint8_t len;
    };
    std::array<Component, 128> stack = {};
    int depth = 0;
    uint64_t current = recordIndex;
    std::array<uint64_t, 128> visited = {};
    int visitCount = 0;

    while (current != 5 && current < totalRecords && depth < 128) {
        bool cycle = false;
        for (int vi = 0; vi < visitCount; vi++) {
            if (visited[vi] == current) {
                cycle = true;
                break;
            }
        }
        if (cycle) {
            break;
        }
        visited[visitCount++] = current;

        if (lookup.nameLens[current] == 0) {
            break;
        }
        stack[depth].nameBytes = lookup.namePool + lookup.nameOffsets[current];
        stack[depth].len = lookup.nameLens[current];
        depth++;
        current = lookup.parents[current];
    }

    uint16_t pos = 0;
    for (int i = depth - 1; i >= 0; i--) {
        if (pos + stack[i].len + 1 >= pathBufSize) {
            break;
        }
        if (pos > 0) {
            pathBuf[pos++] = L'\\';
        }
        const uint8_t* src = stack[i].nameBytes;
        uint8_t len = stack[i].len;
        // Copy with surrogate-pair combining; Clamp to remaining buffer space.
        auto remaining = static_cast<uint16_t>(pathBufSize - pos);
        uint16_t written = CopyNtfsNameUnits(pathBuf + pos, remaining, reinterpret_cast<const WCHAR*>(src),
                                             static_cast<uint16_t>(len));
        pos += written;
    }
    pathBuf[pos] = L'\0';
    return pos;
}

void ProcessRecordSlice(uint8_t* buffer, SliceRange range, uint64_t recordBase, SliceResult* slice,
                        const ScanContext& scan) {
    for (uint64_t i = range.start; i < range.end; i++) {
        MftFileEntry entry;
        if (ScanRecordForEntry(buffer + (FILE_RECORD_SIZE * i), recordBase + i, scan, &entry)) {
            slice->entries.push_back(entry);
        }
    }
}

void ProcessRecordBatch(uint8_t* buffer, uint64_t filesToLoad, uint64_t& recordIndex, EntryBuffer& entries,
                        const ScanContext& scan) {
    for (uint64_t i = 0; i < filesToLoad; i++, recordIndex++) {
        MftFileEntry entry;
        if (!ScanRecordForEntry(buffer + (FILE_RECORD_SIZE * i), recordIndex, scan, &entry)) {
            continue;
        }

        if (entries.usedCount >= entries.capacity) {
            uint64_t newCapacity = entries.capacity * 2;
            auto* grown = ShouldFailAlloc()
                              ? nullptr
                              : static_cast<MftFileEntry*>(realloc(
                                    entries.result->entries, static_cast<size_t>(newCapacity) * sizeof(MftFileEntry)));
            if (grown == nullptr) {
                SetErrorMessage(entries.result->errorMessage, L"Failed to grow entry array");
                entries.result->usedRecords = entries.usedCount;
                return;
            }
            entries.capacity = newCapacity;
            entries.result->entries = grown;
        }

        entries.result->entries[entries.usedCount] = entry;
        entries.usedCount++;
    }
}
