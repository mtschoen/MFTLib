// Included inside linux_smoke_test.cpp's anonymous namespace.

bool verifyFreedRows(const MftParseResult& result, bool resolvePaths) {
    struct ExpectedFreedRecord {
        uint64_t recordNumber;
        uint64_t parentRecordNumber;
        uint16_t sequenceNumber;
        uint16_t flags;
        const char16_t* name;
        const char16_t* path;
    };
    const std::array<ExpectedFreedRecord, 8> expected = {{
        {12, 5, 14, 2, u"deleted-dir", u"deleted-dir"},
        {13, 12, 14, 0, u"deleted-before.txt", u"deleted-dir\\deleted-before.txt"},
        {14, 12, 15, 0, u"deleted-current.txt", u"deleted-dir\\deleted-current.txt"},
        {15, 8, 16, 0, u"deleted-live.txt", u"sub\\deleted-live.txt"},
        {16, 8, 17, MFT_ENTRY_FLAG_PATH_UNRESOLVED, u"deleted-reused.txt", u"deleted-reused.txt"},
        {17, 12, 18, MFT_ENTRY_FLAG_PATH_UNRESOLVED, u"deleted-stale.txt", u"deleted-stale.txt"},
        {21, 13, 22, MFT_ENTRY_FLAG_PATH_UNRESOLVED, u"deleted-under-freed-file.txt", u"deleted-under-freed-file.txt"},
        {22, 6, 23, MFT_ENTRY_FLAG_PATH_UNRESOLVED, u"deleted-under-live-file.txt", u"deleted-under-live-file.txt"},
    }};
    const auto* entries = resolvePaths ? result.pathEntries : result.entries;
    const auto* strings = resolvePaths ? result.pathStrings : result.entryStrings;
    if (result.usedRecords != 16 || entries == nullptr || strings == nullptr) {
        return false;
    }
    for (size_t index = 0; index < expected.size(); ++index) {
        const auto& entry = entries[8 + index];
        const auto& row = expected[index];
        const auto flags =
            resolvePaths ? row.flags : static_cast<uint16_t>(row.flags & ~MFT_ENTRY_FLAG_PATH_UNRESOLVED);
        const std::u16string_view name(reinterpret_cast<const char16_t*>(strings + entry.stringOffset),
                                       entry.stringLength);
        if (entry.recordNumber != row.recordNumber || entry.parentRecordNumber != row.parentRecordNumber ||
            entry.sequenceNumber != row.sequenceNumber || entry.flags != flags ||
            name != (resolvePaths ? row.path : row.name)) {
            std::fprintf(stderr, "  FAIL: freed record %llu flags=%u sequence=%u\n",
                         static_cast<unsigned long long>(entry.recordNumber), entry.flags, entry.sequenceNumber);
            return false;
        }
    }
    return true;
}

bool verifyIncludeFreedScan(const char* path, uint32_t flags) {
    auto* ordinary = ParseMFTFromFileUtf8(path, nullptr, flags, kDefaultBufferRecords);
    auto* includingFreed = ParseMFTFromFileUtf8(path, nullptr, flags | MATCH_FLAG_INCLUDE_FREED, kDefaultBufferRecords);
    const bool resolvePaths = (flags & MATCH_FLAG_RESOLVE_PATHS) != 0;
    bool passed = ordinary != nullptr && includingFreed != nullptr && ordinary->usedRecords == 8 &&
                  ordinary->totalRecords == kFixtureRecordCount && ordinary->errorMessage[0] == L'\0' &&
                  includingFreed->errorMessage[0] == L'\0' && verifyFreedRows(*includingFreed, resolvePaths);
    if (passed) {
        const auto* ordinaryEntries = resolvePaths ? ordinary->pathEntries : ordinary->entries;
        const auto* includedEntries = resolvePaths ? includingFreed->pathEntries : includingFreed->entries;
        const auto* ordinaryStrings = resolvePaths ? ordinary->pathStrings : ordinary->entryStrings;
        const auto* includedStrings = resolvePaths ? includingFreed->pathStrings : includingFreed->entryStrings;
        const auto stringUnits = resolvePaths ? ordinary->pathStringUnits : ordinary->entryStringUnits;
        passed = std::memcmp(ordinaryEntries, includedEntries, 8 * sizeof(MftCompactEntry)) == 0 &&
                 std::memcmp(ordinaryStrings, includedStrings, stringUnits * sizeof(uint16_t)) == 0;
        for (uint64_t index = 0; index < ordinary->usedRecords; ++index) {
            passed = passed && (ordinaryEntries[index].flags & 1U) != 0;
        }
    }
    FreeMftResult(ordinary);
    FreeMftResult(includingFreed);
    return passed;
}

bool testIncludeFreed() {
    constexpr const char* path = "/tmp/mftlib_include_freed.mft";
    if (!GenerateFixtureMFTUtf8(path)) {
        return false;
    }
    const bool passed =
        verifyIncludeFreedScan(path, MATCH_FLAG_NONE) && verifyIncludeFreedScan(path, MATCH_FLAG_RESOLVE_PATHS);
    std::remove(path);
    return passed;
}
