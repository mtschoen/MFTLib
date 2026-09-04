// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.records.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <algorithm>
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

struct StandardInformationValues {
    uint32_t fileAttributes = 0;
    int64_t modifiedTime = 0;
    bool present = false;
};

bool TryExtractStandardInformation(const ATTRIBUTE_RECORD_HEADER* attribute, StandardInformationValues* values) {
    constexpr size_t kResidentHeaderSize = 0x18;
    constexpr size_t kMinStandardInformationSize = 36;
    if (attribute->Form.Resident.ValueOffset < kResidentHeaderSize ||
        static_cast<size_t>(attribute->Form.Resident.ValueOffset) + kMinStandardInformationSize >
            attribute->RecordLength) {
        return false;
    }
    const auto* value = reinterpret_cast<const uint8_t*>(attribute) + attribute->Form.Resident.ValueOffset;
    // $STANDARD_INFORMATION body: 0x00 creation, 0x08 last altered, 0x10 MFT changed,
    // 0x18 last read, 0x20 DOS file permissions. The 36-byte guard above covers all of
    // these, so neither read needs a further bounds check.
    memcpy(&values->modifiedTime, value + 8, sizeof(int64_t));
    memcpy(&values->fileAttributes, value + 32, sizeof(uint32_t));
    values->present = true;
    return true;
}

bool TryExtractFileName(const ATTRIBUTE_RECORD_HEADER* attribute, PFILE_NAME* outNameAttr) {
    constexpr size_t kResidentHeaderSize = 0x18;
    constexpr size_t kMinFileNameHeaderSize = 66;
    if (attribute->Form.Resident.ValueOffset < kResidentHeaderSize ||
        static_cast<size_t>(attribute->Form.Resident.ValueOffset) + kMinFileNameHeaderSize > attribute->RecordLength) {
        return false;
    }
    auto* nameAttr = reinterpret_cast<PFILE_NAME>(const_cast<uint8_t*>(reinterpret_cast<const uint8_t*>(attribute)) +
                                                  attribute->Form.Resident.ValueOffset);
    size_t requiredNameSize = kMinFileNameHeaderSize + (static_cast<size_t>(nameAttr->FileNameLength) * sizeof(WCHAR));
    if (static_cast<size_t>(attribute->Form.Resident.ValueOffset) + requiredNameSize > attribute->RecordLength) {
        return false;
    }
    *outNameAttr = nameAttr;
    return true;
}

// Walk a record's attributes, populating *standardInformation when
// $STANDARD_INFORMATION is seen, and stop at the first resident, non-DOS
// FileName attribute. Returns that FileName attribute, or nullptr if the record
// has none.
PFILE_NAME FindNamedAttribute(PFILE_RECORD_SEGMENT_HEADER rec, ParseGeometry geometry,
                              StandardInformationValues* standardInformation) {
    *standardInformation = {};
    auto* recordPointer = reinterpret_cast<uint8_t*>(rec);
    if (rec->FirstAttributeOffset < 42 || rec->FirstAttributeOffset + sizeof(uint32_t) > geometry.recordSize) {
        return nullptr;
    }
    auto* attribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(recordPointer + rec->FirstAttributeOffset);
    constexpr size_t kResidentHeaderSize = 0x18;
    while (true) {
        const auto offset = static_cast<size_t>(reinterpret_cast<uint8_t*>(attribute) - recordPointer);
        if (offset + sizeof(uint32_t) > geometry.recordSize) {
            return nullptr;
        }
        if (attribute->TypeCode == EndMarker) {
            break;
        }
        if (offset + kResidentHeaderSize > geometry.recordSize || attribute->RecordLength < kResidentHeaderSize ||
            offset + attribute->RecordLength > geometry.recordSize) {
            return nullptr;
        }
        if (attribute->TypeCode == StandardInformation && attribute->FormCode == 0) {
            if (!TryExtractStandardInformation(attribute, standardInformation)) {
                return nullptr;
            }
        } else if (attribute->TypeCode == FileName && attribute->FormCode == 0) {
            PFILE_NAME nameAttr = nullptr;
            if (!TryExtractFileName(attribute, &nameAttr)) {
                return nullptr;
            }
            if (nameAttr->Flags != 2) {
                return nameAttr;
            }
        }
        attribute =
            reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(attribute) + attribute->RecordLength);
    }
    return nullptr;
}

// Scan one file record. If it is an in-use, non-extension record with a non-DOS
// FileName that passes the filter, fill *outEntry and return true. Side effect:
// stores the name into the path-lookup table when one is provided.
bool ScanRecordForEntry(uint8_t* recPtr, uint64_t recordIndex, const ScanContext& scan, ParsedEntry* outEntry) {
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

    StandardInformationValues standardInformation{};
    auto* nameAttr = FindNamedAttribute(rec, scan.geometry, &standardInformation);
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

    outEntry->recordNumber = recordIndex;
    outEntry->parentRecordNumber = parent;
    outEntry->flags = rec->Flags;
    outEntry->fileAttributes =
        standardInformation.present ? standardInformation.fileAttributes : nameAttr->FileAttributes;
    outEntry->modifiedTime = standardInformation.present ? standardInformation.modifiedTime
                                                         : static_cast<int64_t>(nameAttr->ModificationTime);
    outEntry->name = nameAttr->FileName;
    outEntry->nameLength = nameAttr->FileNameLength;
    return true;
}

}  // namespace

bool ResolvePath(uint64_t recordIndex, const PathLookup& lookup, uint64_t totalRecords, std::vector<uint16_t>& path) {
    path.clear();
    struct Component {
        const uint16_t* nameUnits;
        uint8_t len;
    };
    std::array<Component, 128> stack = {};
    int depth = 0;
    uint64_t current = recordIndex;
    std::array<uint64_t, 128> visited = {};
    int visitCount = 0;

    while (current != 5 && current < totalRecords && depth < 128) {
        if (std::find(visited.begin(), visited.begin() + visitCount, current) != visited.begin() + visitCount) {
            break;
        }
        visited[visitCount++] = current;

        if (lookup.nameLens[current] == 0) {
            break;
        }
        stack[depth].nameUnits = reinterpret_cast<const uint16_t*>(lookup.namePool + lookup.nameOffsets[current]);
        stack[depth].len = lookup.nameLens[current];
        depth++;
        current = lookup.parents[current];
    }

    if (depth == 0) {
        return true;
    }

    auto totalUnits = static_cast<uint64_t>(depth - 1);
    for (int i = 0; i < depth; i++) {
        totalUnits += stack[i].len;
    }

    if (totalUnits > MAX_NTFS_PATH_UNITS) {
        return false;
    }

    path.resize(static_cast<size_t>(totalUnits));
    size_t pos = 0;
    for (int i = depth - 1; i >= 0; i--) {
        if (pos > 0) {
            path[pos++] = static_cast<uint16_t>(L'\\');
        }
        const uint16_t* src = stack[i].nameUnits;
        uint8_t len = stack[i].len;
        memcpy(path.data() + pos, src, static_cast<size_t>(len) * sizeof(uint16_t));
        pos += len;
    }
    return true;
}

namespace {

bool EnsureEntryCapacity(CompactOutput& output, uint64_t sliceEntryCount, wchar_t* errorMessage) {
    if (output.entryCount + sliceEntryCount <= output.entryCapacity) {
        return true;
    }
    uint64_t newCapacity = output.entryCapacity == 0 ? 1024 : output.entryCapacity;
    while (output.entryCount + sliceEntryCount > newCapacity) {
        if (newCapacity > UINT64_MAX / 2) {
            SetErrorMessageBuffer(errorMessage, 256, L"Entry array capacity overflow");
            return false;
        }
        newCapacity *= 2;
    }
    if (newCapacity > SIZE_MAX / sizeof(MftCompactEntry)) {
        SetErrorMessageBuffer(errorMessage, 256, L"Entry array capacity overflow");
        return false;
    }
    auto* grown = ShouldFailAlloc() ? nullptr
                                    : static_cast<MftCompactEntry*>(realloc(
                                          output.entries, static_cast<size_t>(newCapacity) * sizeof(MftCompactEntry)));
    if (grown == nullptr) {
        SetErrorMessageBuffer(errorMessage, 256, L"Failed to grow entry array");
        return false;
    }
    output.entries = grown;
    output.entryCapacity = newCapacity;
    return true;
}

bool EnsureStringCapacity(CompactOutput& output, uint64_t sliceStringUnits, wchar_t* errorMessage) {
    if (output.stringUnits + sliceStringUnits <= output.stringCapacity) {
        return true;
    }
    uint64_t newCapacity = output.stringCapacity == 0 ? 1024 : output.stringCapacity;
    while (output.stringUnits + sliceStringUnits > newCapacity) {
        if (newCapacity > UINT64_MAX / 2) {
            SetErrorMessageBuffer(errorMessage, 256, L"String pool capacity overflow");
            return false;
        }
        newCapacity *= 2;
    }
    if (newCapacity > SIZE_MAX / sizeof(uint16_t)) {
        SetErrorMessageBuffer(errorMessage, 256, L"String pool capacity overflow");
        return false;
    }
    auto* grown =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<uint16_t*>(realloc(output.strings, static_cast<size_t>(newCapacity) * sizeof(uint16_t)));
    if (grown == nullptr) {
        SetErrorMessageBuffer(errorMessage, 256, L"Failed to grow string pool");
        return false;
    }
    output.strings = grown;
    output.stringCapacity = newCapacity;
    return true;
}

}  // namespace

bool AppendSlice(CompactOutput& output, const SliceResult& slice, wchar_t* errorMessage) {
    if (slice.entries.empty()) {
        return true;
    }

    uint64_t sliceEntryCount = slice.entries.size();
    uint64_t sliceStringUnits = slice.strings.size();

    if (!EnsureEntryCapacity(output, sliceEntryCount, errorMessage)) {
        return false;
    }

    if (sliceStringUnits > 0 && !EnsureStringCapacity(output, sliceStringUnits, errorMessage)) {
        return false;
    }

    uint64_t baseOffset = output.stringUnits;
    if (sliceStringUnits > 0) {
        memcpy(output.strings + output.stringUnits, slice.strings.data(),
               static_cast<size_t>(sliceStringUnits) * sizeof(uint16_t));
        output.stringUnits += sliceStringUnits;
    }

    for (const auto& compact : slice.entries) {
        MftCompactEntry patched = compact;
        patched.stringOffset += baseOffset;
        output.entries[output.entryCount++] = patched;
    }

    return true;
}

void ProcessRecordSlice(uint8_t* buffer, SliceRange range, uint64_t recordBase, SliceResult* slice,
                        const ScanContext& scan) {
    for (uint64_t i = range.start; i < range.end; i++) {
        ParsedEntry entry{};
        if (ScanRecordForEntry(buffer + (static_cast<size_t>(scan.geometry.recordSize) * i), recordBase + i, scan,
                               &entry)) {
            slice->append(entry);
        }
    }
}

void ProcessRecordBatch(uint8_t* buffer, uint64_t filesToLoad, uint64_t& recordIndex, SliceResult& batchSlice,
                        const ScanContext& scan) {
    for (uint64_t i = 0; i < filesToLoad; i++, recordIndex++) {
        ParsedEntry entry{};
        if (ScanRecordForEntry(buffer + (static_cast<size_t>(scan.geometry.recordSize) * i), recordIndex, scan,
                               &entry)) {
            batchSlice.append(entry);
        }
    }
}
