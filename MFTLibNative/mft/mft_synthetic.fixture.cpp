// Part of the synthetic-generator component. Included by mft_synthetic.cpp from
// inside its anonymous namespace; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT_SYNTHETIC
    #error "mft_synthetic.fixture.cpp is a fragment included by mft_synthetic.cpp"
#endif

struct FixtureDataSpec {
    bool present = false;
    bool resident = false;
    int64_t lowestVcn = 0;
    uint64_t size = 0;
};

struct FixtureRecordSpec {
    uint64_t recordIndex = 0;
    uint16_t headerFlags = 0;
    uint64_t parentRecord = 0;
    const wchar_t* name = nullptr;
    uint8_t nameLength = 0;
    uint32_t fileAttributes = 0;
    bool attributeListPresent = false;
    FixtureDataSpec firstData{};
    FixtureDataSpec secondData{};
};

// Writes one $DATA attribute in the requested form. Resident carries the size in
// ValueLength; non-resident carries it in FileSize, which the reference states is
// only valid when LowestVcn is zero, so the parser must ignore a nonzero-LowestVcn
// record and the fixture writes one deliberately.
uint16_t WriteFixtureDataAttribute(uint8_t* record, uint16_t offset, const FixtureDataSpec& data) {
    if (!data.present) {
        return offset;
    }

    auto* dataAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    dataAttribute->TypeCode = Data;
    dataAttribute->FormCode = data.resident ? static_cast<uint8_t>(0) : static_cast<uint8_t>(1);
    dataAttribute->NameLength = 0;

    if (data.resident) {
        dataAttribute->Form.Resident.ValueLength = static_cast<uint32_t>(data.size);
        dataAttribute->Form.Resident.ValueOffset = 0x18;
        dataAttribute->RecordLength = static_cast<uint16_t>((0x18 + static_cast<uint16_t>(data.size) + 7U) & ~7U);
        return static_cast<uint16_t>(offset + dataAttribute->RecordLength);
    }

    auto clusterSize = static_cast<uint64_t>(4096ULL);
    uint64_t allocatedSize = (data.size + (clusterSize - 1ULL)) & ~(clusterSize - 1ULL);

    dataAttribute->Form.Nonresident.LowestVcn.QuadPart = data.lowestVcn;
    dataAttribute->Form.Nonresident.HighestVcn.QuadPart = 0;
    dataAttribute->Form.Nonresident.MappingPairsOffset = 0x48;
    dataAttribute->Form.Nonresident.AllocatedLength = static_cast<LONGLONG>(allocatedSize);
    dataAttribute->Form.Nonresident.FileSize = static_cast<LONGLONG>(data.size);
    dataAttribute->Form.Nonresident.ValidDataLength = static_cast<LONGLONG>(data.size);
    dataAttribute->Form.Nonresident.TotalAllocated = static_cast<LONGLONG>(allocatedSize);

    auto* runPointer = reinterpret_cast<uint8_t*>(dataAttribute) + 0x48;
    if (data.size == 0ULL) {
        *runPointer = 0;
        dataAttribute->RecordLength = 0x48;
        return static_cast<uint16_t>(offset + dataAttribute->RecordLength);
    }

    const uint64_t clusterCount = (allocatedSize + (clusterSize - 1ULL)) / clusterSize;
    const uint8_t runOffsetFieldBytes = 3;
    uint8_t runLengthFieldBytes = (clusterCount > 0xFFULL) ? 2U : 1U;
    *runPointer++ = static_cast<uint8_t>((runOffsetFieldBytes << 4) | (runLengthFieldBytes & 0x0FU));
    for (uint8_t i = 0; i < runLengthFieldBytes; i++) {
        *runPointer++ = static_cast<uint8_t>(clusterCount >> (i * 8));
    }
    for (uint8_t i = 0; i < runOffsetFieldBytes; i++) {
        *runPointer++ = 0x00;
    }
    *runPointer = 0;
    dataAttribute->RecordLength = static_cast<uint16_t>(
        (0x48U + static_cast<uint16_t>(1U + runLengthFieldBytes + runOffsetFieldBytes + 1U) + 7U) & ~7U);
    return static_cast<uint16_t>(offset + dataAttribute->RecordLength);
}

// Writes a minimal resident $ATTRIBUTE_LIST so a record can carry one without a
// $DATA in the base segment: the size-unknown case from the specification.
uint16_t WriteFixtureAttributeListAttribute(uint8_t* record, uint16_t offset) {
    auto* attributeListAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    attributeListAttribute->TypeCode = AttributeList;
    attributeListAttribute->FormCode = 0;
    attributeListAttribute->Form.Resident.ValueOffset = 0x18;
    attributeListAttribute->Form.Resident.ValueLength = sizeof(ATTRIBUTE_LIST_ENTRY);
    attributeListAttribute->RecordLength = static_cast<uint16_t>((0x18U + sizeof(ATTRIBUTE_LIST_ENTRY) + 7U) & ~7U);

    auto* attributeEntry = reinterpret_cast<PATTRIBUTE_LIST_ENTRY>(record + offset + 0x18);
    attributeEntry->AttributeTypeCode = Data;
    attributeEntry->RecordLength = static_cast<uint16_t>(sizeof(ATTRIBUTE_LIST_ENTRY));
    attributeEntry->AttributeNameLength = 0;
    attributeEntry->AttributeNameOffset = 0;
    attributeEntry->LowestVcn.QuadPart = 0;
    attributeEntry->SegmentReference = {};
    attributeEntry->Reserved = 0;

    return static_cast<uint16_t>(offset + attributeListAttribute->RecordLength);
}

void BuildFixtureRecord(uint8_t* record, const FixtureRecordSpec& spec) {
    memset(record, 0, kFixtureRecordSize);

    auto* header = reinterpret_cast<PFILE_RECORD_SEGMENT_HEADER>(record);
    header->MultiSectorHeader.Magic = 0x454C4946;
    header->MultiSectorHeader.UpdateSequenceArrayOffset = 0x30;
    header->MultiSectorHeader.UpdateSequenceArraySize = static_cast<uint16_t>((kFixtureRecordSize / 512U) + 1U);
    header->SequenceNumber = static_cast<uint16_t>(spec.recordIndex + 1ULL);
    header->Flags = spec.headerFlags;
    header->FirstAttributeOffset = static_cast<uint16_t>(
        (0x30 + (header->MultiSectorHeader.UpdateSequenceArraySize * sizeof(uint16_t)) + 7U) & ~7U);
    memcpy(record + 0x1C, &kFixtureRecordSize, sizeof(kFixtureRecordSize));

    if ((spec.headerFlags & 0x0001U) == 0U) {
        auto* endAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + header->FirstAttributeOffset);
        endAttribute->TypeCode = EndMarker;
        ApplyUSAProtection(record, ParseGeometry{kFixtureRecordSize},
                           static_cast<uint16_t>(spec.recordIndex & 0xFFFFU));
        return;
    }

    uint64_t modifiedTime = kFixtureModifiedBase + (spec.recordIndex * kFixtureModifiedStep);
    bool useFirstDataAttribute = true;
    uint64_t fileSize = 0;
    if (spec.firstData.present) {
        useFirstDataAttribute = !(!spec.firstData.resident && spec.firstData.lowestVcn != 0 && spec.secondData.present);
        fileSize = useFirstDataAttribute ? spec.firstData.size : 0;
    }
    if (!useFirstDataAttribute && spec.secondData.present) {
        fileSize = spec.secondData.size;
    }

    uint64_t allocSize = 0;
    if (spec.firstData.present) {
        if (useFirstDataAttribute) {
            allocSize = spec.firstData.resident ? fileSize : ((fileSize + 4096ULL - 1ULL) & ~(4096ULL - 1ULL));
        } else if (spec.secondData.present) {
            allocSize = (spec.secondData.size + 4096ULL - 1ULL) & ~(4096ULL - 1ULL);
        }
    }
    const SyntheticMeta meta = {
        modifiedTime, modifiedTime, modifiedTime,        modifiedTime,
        fileSize,     allocSize,    spec.fileAttributes, (spec.headerFlags & 0x0002U) != 0U,
    };

    uint8_t computedNameLength = (spec.name != nullptr) ? static_cast<uint8_t>(wcslen(spec.name)) : 0;

    uint16_t offset = header->FirstAttributeOffset;
    offset = WriteStandardInformationAttribute(record, offset, meta);
    auto writeSpec = SyntheticRecordSpec{
        spec.recordIndex, spec.parentRecord, spec.headerFlags, spec.name, computedNameLength, 0, kFixtureRecordSize};
    offset = WriteFileNameAttribute(record, offset, writeSpec, meta);
    if (spec.attributeListPresent) {
        offset = WriteFixtureAttributeListAttribute(record, offset);
    }
    offset = WriteFixtureDataAttribute(record, offset, spec.firstData);
    offset = WriteFixtureDataAttribute(record, offset, spec.secondData);

    auto* endAttribute = reinterpret_cast<PATTRIBUTE_RECORD_HEADER>(record + offset);
    endAttribute->TypeCode = EndMarker;
    ApplyUSAProtection(record, ParseGeometry{kFixtureRecordSize}, static_cast<uint16_t>(spec.recordIndex & 0xFFFFU));
}

bool GenerateFixtureMFTImpl(const char* filePath) {
    const size_t fileSize = static_cast<size_t>(kFixtureRecordCount * kFixtureRecordSize);
    auto* buffer = static_cast<uint8_t*>(ShouldFailAlloc() ? nullptr : mftlib::platform::big_alloc(fileSize));
    if (buffer == nullptr) {
        return false;
    }

    memset(buffer, 0, fileSize);
    BuildFixtureRecord(buffer + (0 * kFixtureRecordSize),
                       {0, 0x0001, 5, L"$MFT", 0, 0x06, false, {true, false, 0, 65536ULL}, {}});
    BuildFixtureRecord(buffer + (5 * kFixtureRecordSize), {5, 0x0003, 5, L".", 0, 0x10, false, {}, {}});
    BuildFixtureRecord(buffer + (6 * kFixtureRecordSize),
                       {6, 0x0001, 5, L"resident.txt", 0, 0x20, false, {true, true, 0, 37ULL}, {}});
    BuildFixtureRecord(buffer + (7 * kFixtureRecordSize),
                       {7, 0x0001, 5, L"big.bin", 0, 0x20, false, {true, false, 0, 1234567ULL}, {}});
    BuildFixtureRecord(buffer + (8 * kFixtureRecordSize), {8, 0x0003, 5, L"sub", 0, 0x10, false, {}, {}});
    BuildFixtureRecord(buffer + (9 * kFixtureRecordSize), {9, 0x0001, 8, L"nodata.dat", 0, 0x20, true, {}, {}});
    BuildFixtureRecord(
        buffer + (10 * kFixtureRecordSize),
        {10, 0x0001, 8, L"split.bin", 0, 0x20, false, {true, false, 8, 999ULL}, {true, false, 0, 4096ULL}});

    auto* file = mftlib::platform::open_write(filePath);
    if (file == nullptr) {
        mftlib::platform::big_free(buffer, fileSize);
        return false;
    }

    int64_t written = mftlib::platform::pwrite_at(file, buffer, fileSize, mftlib::platform::FileOffset{0});
    bool success = (written == static_cast<int64_t>(fileSize));
    mftlib::platform::close_file(file);
    mftlib::platform::big_free(buffer, fileSize);
    return success;
}
