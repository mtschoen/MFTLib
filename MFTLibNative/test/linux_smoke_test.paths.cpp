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

    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixtureMalformedPath, nullptr, 0, 256);
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

    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixtureMalformedPath, nullptr, 0, 256);
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

    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixtureZeroNamePath, nullptr, 0, 256);
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

bool test_path_resolution_and_fallback() {
    if (!generate_fixture()) {
        return false;
    }
    // Path resolution success: matchFlags = MATCH_FLAG_RESOLVE_PATHS
    MftParseResult* parseResult =
        ParseMFTFromFileUtf8(kFixturePath, nullptr, MATCH_FLAG_RESOLVE_PATHS, kDefaultBufferRecords);
    bool hasRootEntry = false;
    bool sequencesMatch = true;
    if (parseResult != nullptr && parseResult->pathEntries != nullptr) {
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
            const MftCompactEntry& entry = parseResult->pathEntries[i];
            if (entry.sequenceNumber != static_cast<uint16_t>(entry.recordNumber + 1)) {
                std::fprintf(stderr, "  FAIL: resolved record %llu sequenceNumber %u, expected %llu\n",
                             static_cast<unsigned long long>(entry.recordNumber),
                             static_cast<unsigned>(entry.sequenceNumber),
                             static_cast<unsigned long long>(entry.recordNumber + 1));
                sequencesMatch = false;
            }
            if (parseResult->pathEntries[i].recordNumber == 5) {
                hasRootEntry =
                    (parseResult->pathEntries[i].parentRecordNumber == 5 &&
                     parseResult->pathEntries[i].stringLength == 0 && (parseResult->pathEntries[i].flags & 1) != 0);
                break;
            }
        }
    }
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 && parseResult->pathEntries != nullptr &&
                      parseResult->pathStrings != nullptr && parseResult->pathStringUnits > 0 &&
                      parseResult->entries == nullptr && parseResult->entryStrings == nullptr &&
                      parseResult->entryStringUnits == 0 && hasRootEntry && sequencesMatch;
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }

    // Path allocation failure fallback: fail the pathEntries allocation
    // Result(1), lookup gate(2), metadata(3-5), buffers(6-7), entries(8), strings(9).
    SetAllocFailCountdown(10);
    MftParseResult* fallbackResult =
        ParseMFTFromFileUtf8(kFixturePath, nullptr, MATCH_FLAG_RESOLVE_PATHS, kDefaultBufferRecords);
    bool fallbackPassed = (fallbackResult != nullptr) && fallbackResult->usedRecords > 0 &&
                          fallbackResult->pathEntries == nullptr && fallbackResult->pathStrings == nullptr &&
                          fallbackResult->entries != nullptr && fallbackResult->entryStrings != nullptr &&
                          fallbackResult->errorMessage[0] == L'\0';
    if (!fallbackPassed && fallbackResult != nullptr) {
        std::fprintf(stderr, "  FAIL: path fallback failed (pathEntries=%p entries=%p err[0]=%d)\n",
                     static_cast<void*>(fallbackResult->pathEntries), static_cast<void*>(fallbackResult->entries),
                     static_cast<int>(fallbackResult->errorMessage[0]));
    }
    if (fallbackResult != nullptr) {
        FreeMftResult(fallbackResult);
    }
    SetAllocFailCountdown(0);
    ResetTestState();
    remove_fixture();
    return testPassed && fallbackPassed;
}

struct ProgressReport {
    MftScanPhase phase;
    uint64_t recordsScanned;
    uint64_t totalRecords;
    double elapsedMs;
};

void CollectProgress(MftScanPhase phase, uint64_t recordsScanned, uint64_t totalRecords, double elapsedMs,
                     void* context) {
    auto* reports = static_cast<std::vector<ProgressReport>*>(context);
    reports->push_back({phase, recordsScanned, totalRecords, elapsedMs});
}

struct ProgressSummary {
    uint64_t parsing = 0;
    uint64_t resolving = 0;
    uint64_t parsingReportCount = 0;
    uint64_t resolvingReportCount = 0;
};

bool CheckMonotonicProgress(const std::vector<ProgressReport>& reports, bool strictParsing, ProgressSummary& summary) {
    for (const auto& report : reports) {
        if (report.phase == MftScanPhase::Parsing) {
            summary.parsingReportCount++;
            if (report.recordsScanned < summary.parsing ||
                (strictParsing && report.recordsScanned == summary.parsing) ||
                report.recordsScanned > report.totalRecords) {
                std::fprintf(stderr, "  FAIL: parsing progress not monotonic (prev=%llu cur=%llu total=%llu)\n",
                             static_cast<unsigned long long>(summary.parsing),
                             static_cast<unsigned long long>(report.recordsScanned),
                             static_cast<unsigned long long>(report.totalRecords));
                return false;
            }
            summary.parsing = report.recordsScanned;
        } else if (report.phase == MftScanPhase::ResolvingPaths) {
            summary.resolvingReportCount++;
            if (report.recordsScanned < summary.resolving || report.recordsScanned > report.totalRecords) {
                std::fprintf(stderr, "  FAIL: resolving progress not monotonic (prev=%llu cur=%llu total=%llu)\n",
                             static_cast<unsigned long long>(summary.resolving),
                             static_cast<unsigned long long>(report.recordsScanned),
                             static_cast<unsigned long long>(report.totalRecords));
                return false;
            }
            summary.resolving = report.recordsScanned;
        }
    }
    return true;
}

bool test_progress_callback() {
    if (!generate_fixture()) {
        return false;
    }
    std::vector<ProgressReport> reports;
    MftParseResult* result =
        ParseMFTFromFileUtf8WithProgress(kFixturePath, nullptr, MATCH_FLAG_RESOLVE_PATHS, 1, nullptr, CollectProgress,
                                      &reports);
    bool ok = (result != nullptr && result->usedRecords > 0);
    if (ok) {
        if (reports.empty()) {
            std::fprintf(stderr, "  FAIL: no progress reports\n");
            ok = false;
        } else {
            ProgressSummary summary;
            ok = CheckMonotonicProgress(reports, true, summary);
            if (ok && summary.parsingReportCount == 0) {
                std::fprintf(stderr, "  FAIL: no Parsing phase reports seen\n");
                ok = false;
            }
            if (ok && summary.resolvingReportCount == 0) {
                std::fprintf(stderr, "  FAIL: no ResolvingPaths phase reports seen\n");
                ok = false;
            }
            if (ok && summary.parsing != result->totalRecords) {
                std::fprintf(stderr, "  FAIL: final parsing report (%llu) != totalRecords (%llu)\n",
                             static_cast<unsigned long long>(summary.parsing),
                             static_cast<unsigned long long>(result->totalRecords));
                ok = false;
            }
            if (ok && summary.resolving != result->usedRecords) {
                std::fprintf(stderr, "  FAIL: final resolving report (%llu) != usedRecords (%llu)\n",
                             static_cast<unsigned long long>(summary.resolving),
                             static_cast<unsigned long long>(result->usedRecords));
                ok = false;
            }
        }
    }
    if (result != nullptr) {
        FreeMftResult(result);
    }
    remove_fixture();
    return ok;
}

// The file export honours the caller's control block the way the volume export does: a parse
// asked to cancel before it starts reports cancellation, and one given an allowance still parses.
bool test_file_parse_control_block() {
    if (!generate_fixture()) {
        return false;
    }
    MftParseControl cancelled = {1, 0};
    MftParseResult* cancelledResult =
        ParseMFTFromFileUtf8WithProgress(kFixturePath, nullptr, 0, 1, &cancelled, nullptr, nullptr);
    bool ok = (cancelledResult != nullptr && cancelledResult->cancelled == 1);
    if (!ok) {
        std::fprintf(stderr, "  FAIL: a control block with cancelRequested set did not cancel the file parse\n");
    }
    if (cancelledResult != nullptr) {
        FreeMftResult(cancelledResult);
    }

    MftParseControl limited = {0, 1};
    MftParseResult* limitedResult =
        ParseMFTFromFileUtf8WithProgress(kFixturePath, nullptr, 0, 1, &limited, nullptr, nullptr);
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

bool test_parallel_progress_monotonicity() {
    constexpr const char* kFixtureParallel = "/tmp/mftlib_parallel_progress.mft";
    constexpr uint64_t kRecordCount = 70000;
    if (!GenerateSyntheticMFTSizedUtf8(kFixtureParallel, kRecordCount, 4096, 1024)) {
        return false;
    }
    SetMaxThreads(8);
    std::vector<ProgressReport> reports;
    MftParseResult* result = ParseMFTFromFileUtf8WithProgress(kFixtureParallel, nullptr, MATCH_FLAG_RESOLVE_PATHS, 4096,
                                                              nullptr, CollectProgress, &reports);
    SetMaxThreads(0);
    ResetTestState();

    bool ok = (result != nullptr && result->usedRecords > 0 && !reports.empty());
    if (ok) {
        ProgressSummary summary;
        ok = CheckMonotonicProgress(reports, false, summary);
        if (ok && summary.resolvingReportCount == 0) {
            std::fprintf(stderr, "  FAIL: no parallel ResolvingPaths phase reports seen\n");
            ok = false;
        }
        if (ok && summary.resolvingReportCount < 16) {
            std::fprintf(stderr, "  FAIL: too few parallel resolving reports (%llu, expected at least 16)\n",
                         static_cast<unsigned long long>(summary.resolvingReportCount));
            ok = false;
        }
    }
    if (result != nullptr) {
        FreeMftResult(result);
    }
    std::remove(kFixtureParallel);
    return ok;
}
