// Part of the mft component. Included by mft.cpp; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.records.cpp is a fragment included by mft.cpp; do not compile it directly"
#endif

#include <algorithm>
#include <array>
#include <cstddef>
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
    if ((filter.flags & MATCH_FLAG_EXACT_MATCH) != 0U) {
        if (nameLen != filter.length) {
            return false;
        }
        return _wcsnicmp(name, filter.text, nameLen) == 0;
    }
    if ((filter.flags & MATCH_FLAG_CONTAINS) != 0U) {
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

struct RecordAttributes {
    PFILE_NAME nameAttribute = nullptr;
    StandardInformationValues standardInformation{};
    int64_t dataSize = 0;
    bool dataPresent = false;
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

enum class DataSizeExtractionResult {
    NotPresent,
    Present,
    Malformed,
};

// Reports the unnamed $DATA size for one attribute. Named streams and non-resident
// records whose lowest virtual cluster number is nonzero do not carry the base size.
DataSizeExtractionResult TryExtractDataSize(const ATTRIBUTE_RECORD_HEADER* attribute, size_t remainingRecordBytes,
                                            int64_t* size) {
    if (attribute->NameLength != 0) {
        return DataSizeExtractionResult::NotPresent;
    }
    if (attribute->FormCode == 0) {
        *size = static_cast<int64_t>(attribute->Form.Resident.ValueLength);
        return DataSizeExtractionResult::Present;
    }
    constexpr size_t kNonresidentHeaderSize = offsetof(ATTRIBUTE_RECORD_HEADER, Form.Nonresident.ValidDataLength) +
                                              sizeof(attribute->Form.Nonresident.ValidDataLength);
    if (attribute->RecordLength < kNonresidentHeaderSize || attribute->RecordLength > remainingRecordBytes) {
        return DataSizeExtractionResult::Malformed;
    }
    if (attribute->Form.Nonresident.LowestVcn.QuadPart != 0 || attribute->Form.Nonresident.FileSize < 0) {
        return DataSizeExtractionResult::NotPresent;
    }
    *size = attribute->Form.Nonresident.FileSize;
    return DataSizeExtractionResult::Present;
}

bool ScanRecordAttributes(PFILE_RECORD_SEGMENT_HEADER record, ParseGeometry geometry,
                          RecordAttributes* recordAttributes) {
    *recordAttributes = {};
    auto* recordPointer = reinterpret_cast<uint8_t*>(record);
    if (record->FirstAttributeOffset < 42 || record->FirstAttributeOffset + sizeof(uint32_t) > geometry.recordSize) {
        return false;
    }
    auto* attribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(recordPointer + record->FirstAttributeOffset);
    constexpr size_t kResidentHeaderSize = 0x18;
    while (true) {
        const auto offset = static_cast<size_t>(reinterpret_cast<uint8_t*>(attribute) - recordPointer);
        if (offset + sizeof(uint32_t) > geometry.recordSize) {
            return false;
        }
        if (attribute->TypeCode == EndMarker) {
            break;
        }
        if (offset + kResidentHeaderSize > geometry.recordSize || attribute->RecordLength < kResidentHeaderSize ||
            offset + attribute->RecordLength > geometry.recordSize) {
            return false;
        }
        if (attribute->TypeCode == StandardInformation && attribute->FormCode == 0) {
            if (!TryExtractStandardInformation(attribute, &recordAttributes->standardInformation)) {
                return false;
            }
        } else if (attribute->TypeCode == FileName && attribute->FormCode == 0) {
            PFILE_NAME nameAttribute = nullptr;
            if (!TryExtractFileName(attribute, &nameAttribute)) {
                return false;
            }
            if (recordAttributes->nameAttribute == nullptr && nameAttribute->Flags != 2) {
                recordAttributes->nameAttribute = nameAttribute;
            }
        } else if (attribute->TypeCode == Data && !recordAttributes->dataPresent) {
            const auto dataSizeResult =
                TryExtractDataSize(attribute, geometry.recordSize - offset, &recordAttributes->dataSize);
            if (dataSizeResult == DataSizeExtractionResult::Malformed) {
                return false;
            }
            recordAttributes->dataPresent = dataSizeResult == DataSizeExtractionResult::Present;
        }
        attribute =
            reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(reinterpret_cast<uint8_t*>(attribute) + attribute->RecordLength);
    }
    return true;
}

// Scan one eligible base record. If it has a validated non-DOS
// FileName that passes the filter, fill *outEntry and return true. Side effect:
// stores the name into the path-lookup table when one is provided.
bool ScanRecordForEntry(uint8_t* recPtr, uint64_t recordIndex, const ScanContext& scan, ParsedEntry* outEntry) {
    auto* rec = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(recPtr);

    if (rec->MultiSectorHeader.Magic != kFileRecordMagic) {
        return false;
    }
    if ((rec->Flags & kRecordInUse) == 0 && (scan.filter.flags & MATCH_FLAG_INCLUDE_FREED) == 0) {
        return false;
    }

    uint64_t baseRef = static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberLowPart) |
                       (static_cast<uint64_t>(rec->BaseFileRecordSegment.SegmentNumberHighPart) << 32);
    if (baseRef != 0) {
        return false;
    }
    // A freed extension record can still carry its base reference; a base record's
    // whole reference, sequence included, is zero.
    if ((rec->Flags & kRecordInUse) == 0 && rec->BaseFileRecordSegment.SequenceNumber != 0) {
        return false;
    }

    RecordAttributes attributes{};
    if (!ScanRecordAttributes(rec, scan.geometry, &attributes) || attributes.nameAttribute == nullptr) {
        return false;
    }
    auto* nameAttr = attributes.nameAttribute;

    bool isDirectory = (rec->Flags & kRecordDirectory) != 0;
    outEntry->flags = rec->Flags;
    if (isDirectory) {
        outEntry->size = 0;
    } else if (attributes.dataPresent) {
        outEntry->size = attributes.dataSize;
    } else {
        outEntry->size = 0;
        outEntry->flags |= MFT_ENTRY_FLAG_SIZE_UNKNOWN;
    }

    uint64_t parent = static_cast<uint64_t>(nameAttr->ParentDirectory.SegmentNumberLowPart) |
                      (static_cast<uint64_t>(nameAttr->ParentDirectory.SegmentNumberHighPart) << 32);

    if ((scan.lookup != nullptr) && recordIndex < scan.totalRecords) {
        scan.lookup->storeName(recordIndex, *rec, *nameAttr);
    }

    if ((scan.filter.text != nullptr) && !FileNameMatches(nameAttr->FileName, nameAttr->FileNameLength, scan.filter)) {
        return false;
    }

    outEntry->recordNumber = recordIndex;
    outEntry->parentRecordNumber = parent;
    outEntry->fileAttributes = attributes.standardInformation.present ? attributes.standardInformation.fileAttributes
                                                                      : nameAttr->FileAttributes;
    outEntry->sequenceNumber = rec->SequenceNumber;
    outEntry->parentSequenceNumber = nameAttr->ParentDirectory.SequenceNumber;
    outEntry->modifiedTime = attributes.standardInformation.present ? attributes.standardInformation.modifiedTime
                                                                    : static_cast<int64_t>(nameAttr->ModificationTime);
    outEntry->name = nameAttr->FileName;
    outEntry->nameLength = nameAttr->FileNameLength;
    return true;
}

// A freed record's path is trusted one hop at a time: the parent must be a validated
// directory whose stored sequence matches the child's reference, or, when the parent
// was freed too, is one higher, since NTFS bumps the sequence when it frees a record.
bool TrustsParentHop(const PathLookup& lookup, uint64_t child, uint64_t parent, uint64_t totalRecords) {
    if (parent >= totalRecords) {
        return false;
    }
    const uint8_t parentFlags = lookup.recordFlags[parent];
    if (parentFlags == PathLookup::kMissingRecord || (parentFlags & PathLookup::kDirectory) == 0) {
        return false;
    }
    const uint16_t referencedSequence = lookup.parentSequenceNumbers[child];
    const uint16_t storedSequence = lookup.sequenceNumbers[parent];
    if (storedSequence == referencedSequence) {
        return true;
    }
    return (parentFlags & PathLookup::kInUse) == 0 && storedSequence == static_cast<uint16_t>(referencedSequence + 1U);
}

bool IsValidatedInUse(const PathLookup& lookup, uint64_t recordIndex) {
    const uint8_t flags = lookup.recordFlags[recordIndex];
    return flags != PathLookup::kMissingRecord && (flags & PathLookup::kInUse) != 0;
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
    const bool freedOrigin = recordIndex < totalRecords && lookup.isValidatedFreed(recordIndex);

    while (current != 5 && current < totalRecords && depth < 128) {
        if (std::find(visited.begin(), visited.begin() + visitCount, current) != visited.begin() + visitCount) {
            break;
        }
        visited[visitCount++] = current;

        if (lookup.nameLens[current] == 0 || (!freedOrigin && !IsValidatedInUse(lookup, current))) {
            break;
        }
        stack[depth].nameUnits = reinterpret_cast<const uint16_t*>(lookup.namePool + lookup.nameOffsets[current]);
        stack[depth].len = lookup.nameLens[current];
        depth++;
        const uint64_t parent = lookup.parents[current];
        if (freedOrigin && !TrustsParentHop(lookup, current, parent, totalRecords)) {
            return false;
        }
        current = parent;
    }

    if (freedOrigin && current != 5) {
        return false;
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

struct CapacityMessages {
    const wchar_t* overflow;
    const wchar_t* allocationFailure;
};

template <typename Element>
bool EnsureCapacity(Element*& data, uint64_t& capacity, uint64_t used, uint64_t extra, MftMessageChar* errorMessage,
                    const CapacityMessages& messages) {
    if (used + extra <= capacity) {
        return true;
    }
    uint64_t newCapacity = capacity == 0 ? 1024 : capacity;
    while (used + extra > newCapacity) {
        if (newCapacity > UINT64_MAX / 2) {
            SetErrorMessageBuffer(errorMessage, 256, messages.overflow);
            return false;
        }
        newCapacity *= 2;
    }
    if (newCapacity > SIZE_MAX / sizeof(Element)) {
        SetErrorMessageBuffer(errorMessage, 256, messages.overflow);
        return false;
    }
    auto* grown = ShouldFailAlloc()
                      ? nullptr
                      : static_cast<Element*>(realloc(data, static_cast<size_t>(newCapacity) * sizeof(Element)));
    if (grown == nullptr) {
        SetErrorMessageBuffer(errorMessage, 256, messages.allocationFailure);
        return false;
    }
    data = grown;
    capacity = newCapacity;
    return true;
}

}  // namespace

bool AppendSlice(CompactOutput& output, const SliceResult& slice, MftMessageChar* errorMessage) {
    if (slice.entries.empty()) {
        return true;
    }

    uint64_t sliceEntryCount = slice.entries.size();
    uint64_t sliceStringUnits = slice.strings.size();

    if (!EnsureCapacity(output.entries, output.entryCapacity, output.entryCount, sliceEntryCount, errorMessage,
                        {L"Entry array capacity overflow", L"Failed to grow entry array"})) {
        return false;
    }

    if (sliceStringUnits > 0 &&
        !EnsureCapacity(output.strings, output.stringCapacity, output.stringUnits, sliceStringUnits, errorMessage,
                        {L"String pool capacity overflow", L"Failed to grow string pool"})) {
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
