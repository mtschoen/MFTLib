// Included inside linux_smoke_test.cpp's anonymous namespace.

// A dump never offers freed rows: the fixture's eight freed base records stay out of a dump
// parse, and every row the parse does emit carries the in-use bit.
bool test_dump_excludes_freed() {
    constexpr const char* path = "/tmp/mftlib_dump_excludes_freed.mft";
    if (!GenerateFixtureMFTUtf8(path)) {
        return false;
    }
    MftParseResult* parseResult = parse_dump(path, kDefaultBufferRecords);
    bool passed = parseResult != nullptr && parseResult->errorMessage[0] == 0 &&
                  parseResult->totalRecords == kFixtureRecordCount && parseResult->usedRecords == 8;
    for (uint64_t index = 0; passed && index < parseResult->usedRecords; ++index) {
        const MftCompactEntry& entry = parseResult->entries[index];
        if ((entry.flags & 1U) == 0 || entry.recordNumber >= 12) {
            std::fprintf(stderr, "  FAIL: record %llu flags=%u came out of a dump parse\n",
                         static_cast<unsigned long long>(entry.recordNumber), entry.flags);
            passed = false;
        }
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(path);
    return passed;
}
