// Included inside linux_smoke_test.cpp's anonymous namespace.
// The owned dump input: one open file is inspected and parsed, and hostile content is rejected.

constexpr const char* kDumpInputPath = "/tmp/mftlib_dump_input.mft";
constexpr uint64_t kDumpInputRecords = 20;

bool write_dump_input_fixture() {
    return GenerateSyntheticMFTSizedUtf8(kDumpInputPath, kDumpInputRecords, 256, 1024);
}

// Overwrites bytes of the fixture at offset.
bool patch_dump_input(long offset, const uint8_t* bytes, size_t count) {
    FILE* fileHandle = std::fopen(kDumpInputPath, "r+b");
    if (fileHandle == nullptr) {
        return false;
    }
    std::fseek(fileHandle, offset, SEEK_SET);
    const bool written = std::fwrite(bytes, 1, count, fileHandle) == count;
    std::fclose(fileHandle);
    return written;
}

// Opens the fixture expecting a rejection and checks the message and the invalid-input flag.
bool dump_input_is_rejected(const char* path, const char* message, uint32_t invalidInput) {
    MftDumpInputInfo info{};
    MftDumpInput* input = OpenMftDumpInput(path, &info);
    const bool rejected = input == nullptr && info.invalidInput == invalidInput &&
                          (message == nullptr ? info.errorMessage[0] != 0 : message_is(info.errorMessage, message));
    if (!rejected) {
        std::fprintf(stderr, "  FAIL: %s: input=%p invalidInput=%u message[0]=%d\n", path, static_cast<void*>(input),
                     info.invalidInput, static_cast<int>(info.errorMessage[0]));
    }
    CloseMftDumpInput(input);
    return rejected;
}

bool test_dump_input_round_trip() {
    if (!write_dump_input_fixture()) {
        return false;
    }
    MftDumpInputInfo info{};
    MftDumpInput* input = OpenMftDumpInput(kDumpInputPath, &info);
    bool testPassed = input != nullptr && info.lengthBytes == kDumpInputRecords * 1024 && info.recordSize == 1024 &&
                      info.invalidInput == 0 && info.errorMessage[0] == 0;
    if (testPassed) {
        // The path is removed after the open: the parse still reads the opened file.
        std::remove(kDumpInputPath);
        MftParseResult* parseResult = ParseMftDumpInput(input, 4, nullptr, nullptr, nullptr);
        testPassed = parseResult != nullptr && parseResult->errorMessage[0] == 0 && parseResult->invalidInput == 0 &&
                     parseResult->totalRecords == kDumpInputRecords && parseResult->usedRecords > 0 &&
                     parseResult->abiVersion == MFT_NATIVE_ABI_VERSION && parseResult->entryStride == 52;
        if (parseResult != nullptr) {
            FreeMftResult(parseResult);
        }
    }
    CloseMftDumpInput(input);
    std::remove(kDumpInputPath);
    return testPassed;
}

bool test_dump_input_rejections() {
    bool testPassed = dump_input_is_rejected("/tmp/mftlib_dump_input_absent.mft", nullptr, 0);

    FILE* empty = std::fopen(kDumpInputPath, "wb");
    if (empty == nullptr) {
        return false;
    }
    std::fclose(empty);
    testPassed = dump_input_is_rejected(kDumpInputPath, "The dump file is empty.", 1) && testPassed;

    const std::array<uint8_t, 4> badSignature = {'B', 'A', 'A', 'D'};
    testPassed = write_dump_input_fixture() && patch_dump_input(0, badSignature.data(), badSignature.size()) &&
                 dump_input_is_rejected(kDumpInputPath, "Invalid or unsupported MFT record size.", 1) && testPassed;

    const std::array<uint8_t, 4> hugeRecordSize = {0xFF, 0xFF, 0xFF, 0xFF};
    testPassed = write_dump_input_fixture() && patch_dump_input(0x1C, hugeRecordSize.data(), hugeRecordSize.size()) &&
                 dump_input_is_rejected(kDumpInputPath, "Invalid or unsupported MFT record size.", 1) && testPassed;

    testPassed = write_dump_input_fixture() && truncate(kDumpInputPath, (kDumpInputRecords * 1024) - 1) == 0 &&
                 dump_input_is_rejected(kDumpInputPath, "File size is not a whole multiple of record size.", 1) &&
                 testPassed;

    // A file cut inside its first header is input that could not be read, not a record size.
    testPassed = write_dump_input_fixture() && truncate(kDumpInputPath, 31) == 0 &&
                 dump_input_is_rejected(kDumpInputPath, "The dump file could not be read completely.", 1) &&
                 testPassed;
    testPassed = write_dump_input_fixture() && truncate(kDumpInputPath, 1) == 0 &&
                 dump_input_is_rejected(kDumpInputPath, "The dump file could not be read completely.", 1) &&
                 testPassed;

    std::remove(kDumpInputPath);
    return testPassed;
}

// Parses the fixture after giving record 10 a hostile update sequence array.
bool dump_input_fixup_is_rejected(uint16_t arrayOffset, uint16_t arraySize) {
    const std::array<uint8_t, 4> hostileArray = {
        static_cast<uint8_t>(arrayOffset & 0xFF), static_cast<uint8_t>(arrayOffset >> 8),
        static_cast<uint8_t>(arraySize & 0xFF), static_cast<uint8_t>(arraySize >> 8)};
    if (!write_dump_input_fixture() || !patch_dump_input((10 * 1024) + 4, hostileArray.data(), hostileArray.size())) {
        return false;
    }
    MftParseResult* parseResult = parse_dump(kDumpInputPath, 256);
    const bool rejected = parseResult != nullptr && parseResult->invalidInput == 1 &&
                          message_is(parseResult->errorMessage, "The dump contains an invalid MFT record fixup.");
    if (!rejected) {
        std::fprintf(stderr, "  FAIL: array offset %u size %u was not rejected\n", arrayOffset, arraySize);
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    return rejected;
}

bool test_dump_input_hostile_fixups() {
    bool testPassed = dump_input_fixup_is_rejected(0xFFF0, 3);
    testPassed = dump_input_fixup_is_rejected(600, 3) && testPassed;
    testPassed = dump_input_fixup_is_rejected(8, 3) && testPassed;
    testPassed = dump_input_fixup_is_rejected(506, 3) && testPassed;
    testPassed = dump_input_fixup_is_rejected(0x30, 0) && testPassed;
    testPassed = dump_input_fixup_is_rejected(0x30, 1) && testPassed;
    testPassed = dump_input_fixup_is_rejected(0x30, 2) && testPassed;
    testPassed = dump_input_fixup_is_rejected(0x30, 4) && testPassed;
    testPassed = dump_input_fixup_is_rejected(0xFFFF, 0xFFFF) && testPassed;
    std::remove(kDumpInputPath);
    return testPassed;
}

// A file that shrinks or grows after the open fails the parse instead of ending it early.
bool dump_input_changed_length_is_incomplete(off_t newLength) {
    if (!write_dump_input_fixture()) {
        return false;
    }
    MftDumpInputInfo info{};
    MftDumpInput* input = OpenMftDumpInput(kDumpInputPath, &info);
    bool testPassed = input != nullptr && truncate(kDumpInputPath, newLength) == 0;
    if (testPassed) {
        MftParseResult* parseResult = ParseMftDumpInput(input, 4, nullptr, nullptr, nullptr);
        testPassed = parseResult != nullptr && parseResult->invalidInput == 1 && parseResult->entries == nullptr &&
                     message_is(parseResult->errorMessage, "The dump file could not be read completely.");
        if (parseResult != nullptr) {
            FreeMftResult(parseResult);
        }
    }
    CloseMftDumpInput(input);
    return testPassed;
}

bool test_dump_input_changed_length() {
    bool testPassed = dump_input_changed_length_is_incomplete(0);
    testPassed = dump_input_changed_length_is_incomplete(6 * 1024) && testPassed;
    testPassed = dump_input_changed_length_is_incomplete((kDumpInputRecords + 1) * 1024) && testPassed;
    std::remove(kDumpInputPath);
    return testPassed;
}

bool test_dump_input_null_arguments() {
    MftParseResult* parseResult = ParseMftDumpInput(nullptr, 4, nullptr, nullptr, nullptr);
    const bool testPassed = OpenMftDumpInput(kDumpInputPath, nullptr) == nullptr && parseResult != nullptr &&
                            parseResult->invalidInput == 0 &&
                            message_is(parseResult->errorMessage, "Dump input is invalid");
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    CloseMftDumpInput(nullptr);
    return testPassed;
}
