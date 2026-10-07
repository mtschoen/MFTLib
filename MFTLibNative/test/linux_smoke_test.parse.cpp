// Included inside linux_smoke_test.cpp's anonymous namespace.
bool test_malformed_attribute_offset() {
    constexpr const char* kFixtureMalformedPath = "/tmp/mftlib_malformed_attr.mft";
    if (!GenerateSyntheticMFTSizedUtf8(kFixtureMalformedPath, 20, 256, 1024)) {
        return false;
    }
    FILE* fileHandle = std::fopen(kFixtureMalformedPath, "r+b");
    if (fileHandle == nullptr) {
        return false;
    }
    const std::array<uint8_t, 2> badOffset = {0x60, 0xEA};
    std::fseek(fileHandle, static_cast<long>((6 * 1024) + 0x38 + 0x14), SEEK_SET);
    std::fwrite(badOffset.data(), 1, badOffset.size(), fileHandle);
    std::fclose(fileHandle);

    MftParseResult* parseResult = parse_dump(kFixtureMalformedPath, 256);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 && parseResult->errorMessage[0] == L'\0';
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixtureMalformedPath);
    return testPassed;
}

// Locates the offset, within one on-disk record buffer, of the first unnamed
// $DATA attribute by walking the attribute chain from the record header's
// FirstAttributeOffset. Returns false when no such attribute is found before the
// end marker or the record buffer runs out, leaving *outOffset unset.
bool FindUnnamedDataAttributeOffset(const std::vector<uint8_t>& recordBuffer, uint16_t* outOffset) {
    const auto* header = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(recordBuffer.data());
    uint16_t attributeOffset = header->FirstAttributeOffset;
    while (static_cast<size_t>(attributeOffset) + sizeof(uint32_t) <= recordBuffer.size()) {
        const auto* attribute = reinterpret_cast<const ATTRIBUTE_RECORD_HEADER*>(recordBuffer.data() + attributeOffset);
        if (attribute->TypeCode == EndMarker) {
            return false;
        }
        if (attribute->TypeCode == Data && attribute->NameLength == 0) {
            *outOffset = attributeOffset;
            return true;
        }
        if (attribute->RecordLength == 0) {
            return false;
        }
        attributeOffset = static_cast<uint16_t>(attributeOffset + attribute->RecordLength);
    }
    return false;
}

// Regression for the guard in TryExtractDataSize (mft.records.cpp) that rejects a
// non-resident $DATA attribute too short to hold Form.Nonresident.FileSize. Record 7's
// real unnamed $DATA attribute is truncated to 24 bytes in place, which leaves the
// leftover, untouched bytes at the attribute's real FileSize offset (48-56) still
// holding the fixture's original 1234567 value. The pre-fix parser has no guard
// against a short non-resident attribute and reads that leftover FileSize, silently
// accepting the malformed record with the original size. The fixed parser rejects any
// non-resident attribute whose RecordLength cannot hold FileSize, so record 7 is
// dropped entirely. The attribute's real offset is found at runtime by walking the
// attribute chain rather than hard-coded, because it depends on the fixture's record
// layout (name length, preceding attribute sizes) and drifts silently if that layout
// changes.
bool test_malformed_nonresident_data_length() {
    constexpr const char* kFixtureMalformedPath = "/tmp/mftlib_malformed_nonresident_data_length.mft";
    constexpr uint64_t kTargetRecordNumber = 7;
    constexpr uint32_t kShortAttributeLength = 24;
    if (!GenerateFixtureMFTUtf8(kFixtureMalformedPath)) {
        return false;
    }

    const long recordFileOffset = static_cast<long>(kTargetRecordNumber * kFixtureRecordSize);
    std::vector<uint8_t> recordBuffer(kFixtureRecordSize);
    FILE* fileHandle = std::fopen(kFixtureMalformedPath, "r+b");
    if (fileHandle == nullptr) {
        std::remove(kFixtureMalformedPath);
        return false;
    }
    std::fseek(fileHandle, recordFileOffset, SEEK_SET);
    bool readOk = std::fread(recordBuffer.data(), 1, recordBuffer.size(), fileHandle) == recordBuffer.size();

    uint16_t dataAttributeOffset = 0;
    if (!readOk || !FindUnnamedDataAttributeOffset(recordBuffer, &dataAttributeOffset)) {
        std::fprintf(stderr,
                     "  FAIL: malformed_nonresident_data_length: could not locate record %llu's "
                     "unnamed $DATA attribute\n",
                     static_cast<unsigned long long>(kTargetRecordNumber));
        std::fclose(fileHandle);
        std::remove(kFixtureMalformedPath);
        return false;
    }

    ATTRIBUTE_RECORD_HEADER malformedAttribute{};
    malformedAttribute.TypeCode = Data;
    malformedAttribute.RecordLength = kShortAttributeLength;
    malformedAttribute.FormCode = 1;
    malformedAttribute.NameLength = 0;
    malformedAttribute.Form.Nonresident.LowestVcn.QuadPart = 0;
    const long attributeFileOffset = recordFileOffset + dataAttributeOffset;
    std::fseek(fileHandle, attributeFileOffset, SEEK_SET);
    std::fwrite(&malformedAttribute, 1, kShortAttributeLength, fileHandle);

    const uint32_t endMarker = static_cast<uint32_t>(EndMarker);
    std::fseek(fileHandle, attributeFileOffset + kShortAttributeLength, SEEK_SET);
    std::fwrite(&endMarker, 1, sizeof(endMarker), fileHandle);
    std::fclose(fileHandle);

    MftParseResult* parseResult = parse_dump(kFixtureMalformedPath, 256);
    bool testPassed = (parseResult != nullptr) && parseResult->errorMessage[0] == L'\0';
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: malformed_nonresident_data_length: usedRecords=%llu errorMessage[0]=%d\n",
                     parseResult != nullptr ? static_cast<unsigned long long>(parseResult->usedRecords) : 0ULL,
                     parseResult != nullptr ? static_cast<int>(parseResult->errorMessage[0]) : -1);
    }
    if (testPassed) {
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
            if (parseResult->entries[i].recordNumber == kTargetRecordNumber) {
                std::fprintf(stderr, "  FAIL: malformed record %llu was accepted (size=%lld)\n",
                             static_cast<unsigned long long>(kTargetRecordNumber),
                             static_cast<long long>(parseResult->entries[i].size));
                testPassed = false;
                break;
            }
        }
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixtureMalformedPath);
    return testPassed;
}

bool test_zero_length_file_name() {
    constexpr const char* kFixtureZeroNamePath = "/tmp/mftlib_zero_name.mft";
    if (!GenerateSyntheticMFTSizedUtf8(kFixtureZeroNamePath, 20, 256, 1024)) {
        return false;
    }
    // Mutate record 6's $FILE_NAME length to zero (offset: record 6 + 0x38 (SI ~0x60) + FN offset ~0x98 + 0x40 =
    // FileNameLength at +0x18+0x40 = +0x58) In synthetic MFT: record 6 header is 0x38. StandardInformation is resident
    // 0x48 + 0x18 = 0x60 length -> next attr at 0x98. At 0x98, FileName attribute header (0x18 resident header).
    // Resident ValueOffset is 0x18 (byte 0x14 of header). FileName struct starts at 0x98 + 0x18 = 0xB0. FileNameLength
    // is byte at offset 0xB0 + 0x40 = 0xF0.
    FILE* fileHandle = std::fopen(kFixtureZeroNamePath, "r+b");
    if (fileHandle == nullptr) {
        return false;
    }
    uint8_t zeroLength = 0;
    std::fseek(fileHandle, static_cast<long>((6 * 1024) + 0xF0), SEEK_SET);
    std::fwrite(&zeroLength, 1, 1, fileHandle);
    std::fclose(fileHandle);

    MftParseResult* parseResult = parse_dump(kFixtureZeroNamePath, 256);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 &&
                      parseResult->errorMessage[0] == L'\0' && parseResult->entryStrings != nullptr;
    if (testPassed) {
        // Find record 6 in entries
        bool foundRecord6 = false;
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
            if (parseResult->entries[i].recordNumber == 6) {
                foundRecord6 = true;
                if (parseResult->entries[i].stringLength != 0) {
                    testPassed = false;
                    std::fprintf(stderr, "  FAIL: record 6 stringLength=%u, expected 0\n",
                                 parseResult->entries[i].stringLength);
                }
                break;
            }
        }
        if (!foundRecord6) {
            testPassed = false;
            std::fprintf(stderr, "  FAIL: record 6 not found in usedRecords\n");
        }
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixtureZeroNamePath);
    return testPassed;
}

struct ProgressReport {
    uint64_t recordsScanned;
    uint64_t totalRecords;
    double elapsedMs;
};

void CollectProgress(uint64_t recordsScanned, uint64_t totalRecords, double elapsedMs, void* context) {
    auto* reports = static_cast<std::vector<ProgressReport>*>(context);
    reports->push_back({recordsScanned, totalRecords, elapsedMs});
}

// True when every report advances the scanned count and never passes the total, and the last
// report is the whole file.
bool CheckMonotonicProgress(const std::vector<ProgressReport>& reports, uint64_t totalRecords) {
    uint64_t previous = 0;
    for (const auto& report : reports) {
        if (report.recordsScanned <= previous || report.recordsScanned > report.totalRecords ||
            report.totalRecords != totalRecords) {
            std::fprintf(stderr, "  FAIL: progress not monotonic (prev=%llu cur=%llu total=%llu)\n",
                         static_cast<unsigned long long>(previous),
                         static_cast<unsigned long long>(report.recordsScanned),
                         static_cast<unsigned long long>(report.totalRecords));
            return false;
        }
        previous = report.recordsScanned;
    }
    if (previous != totalRecords) {
        std::fprintf(stderr, "  FAIL: final progress report (%llu) != totalRecords (%llu)\n",
                     static_cast<unsigned long long>(previous), static_cast<unsigned long long>(totalRecords));
        return false;
    }
    return true;
}

bool test_progress_callback() {
    if (!generate_fixture()) {
        return false;
    }
    std::vector<ProgressReport> reports;
    MftParseResult* result = parse_dump(kFixturePath, 1, nullptr, CollectProgress, &reports);
    bool ok = result != nullptr && result->usedRecords > 0 && !reports.empty() &&
              CheckMonotonicProgress(reports, result->totalRecords);
    if (ok && reports.size() != result->totalRecords) {
        std::fprintf(stderr, "  FAIL: %llu one-record chunks gave %llu progress reports\n",
                     static_cast<unsigned long long>(result->totalRecords),
                     static_cast<unsigned long long>(reports.size()));
        ok = false;
    }
    if (result != nullptr) {
        FreeMftResult(result);
    }
    remove_fixture();
    return ok;
}

// The dump export honours the caller's control block the way the volume export does: a parse
// asked to cancel before it starts reports cancellation, and one given an allowance still parses.
bool test_file_parse_control_block() {
    if (!generate_fixture()) {
        return false;
    }
    MftParseControl cancelled = {1, 0};
    MftParseResult* cancelledResult = parse_dump(kFixturePath, 1, &cancelled);
    bool ok = (cancelledResult != nullptr && cancelledResult->cancelled == 1);
    if (!ok) {
        std::fprintf(stderr, "  FAIL: a control block with cancelRequested set did not cancel the file parse\n");
    }
    if (cancelledResult != nullptr) {
        FreeMftResult(cancelledResult);
    }

    MftParseControl limited = {0, 1};
    MftParseResult* limitedResult = parse_dump(kFixturePath, 1, &limited);
    if (ok && (limitedResult == nullptr || limitedResult->cancelled != 0 || limitedResult->usedRecords == 0)) {
        std::fprintf(stderr, "  FAIL: a one-thread allowance did not parse the file\n");
        ok = false;
    }
    if (limitedResult != nullptr) {
        FreeMftResult(limitedResult);
    }
    remove_fixture();
    return ok;
}

// Chunks parsed across eight workers still report progress once per chunk, in order.
bool test_parallel_progress_monotonicity() {
    constexpr const char* kFixtureParallel = "/tmp/mftlib_parallel_progress.mft";
    constexpr uint64_t kRecordCount = 70000;
    constexpr uint32_t kChunkRecords = 4096;
    if (!GenerateSyntheticMFTSizedUtf8(kFixtureParallel, kRecordCount, kChunkRecords, 1024)) {
        return false;
    }
    SetMaxThreads(8);
    std::vector<ProgressReport> reports;
    MftParseResult* result = parse_dump(kFixtureParallel, kChunkRecords, nullptr, CollectProgress, &reports);
    SetMaxThreads(0);
    ResetTestState();

    constexpr uint64_t kExpectedChunks = (kRecordCount + kChunkRecords - 1) / kChunkRecords;
    bool ok = result != nullptr && result->usedRecords > 0 && CheckMonotonicProgress(reports, kRecordCount);
    if (ok && reports.size() != kExpectedChunks) {
        std::fprintf(stderr, "  FAIL: %llu chunks gave %llu progress reports\n",
                     static_cast<unsigned long long>(kExpectedChunks), static_cast<unsigned long long>(reports.size()));
        ok = false;
    }
    if (result != nullptr) {
        FreeMftResult(result);
    }
    std::remove(kFixtureParallel);
    return ok;
}
