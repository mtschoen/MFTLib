// linux_smoke_test.cpp - native end-to-end + error-path tests on POSIX.
#include <array>
#include <cstdint>
#include <cstdio>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

#include "mft/mft_fixture.h"
#include "mft_api.h"
#include "ntfs.h"

extern "C" bool GenerateSyntheticMFTSizedUtf8(const char* filePath, uint64_t recordCount, uint32_t bufferSizeRecords,
                                              uint32_t recordSize);
extern "C" bool GenerateFixtureMFTUtf8(const char* filePath);
extern "C" void FreeMftResult(MftParseResult* result);
extern "C" MftDumpInput* OpenMftDumpInput(const char* filePathUtf8, MftDumpInputInfo* info);
extern "C" MftParseResult* ParseMftDumpInput(const MftDumpInput* input, uint32_t bufferSizeRecords,
                                             const MftParseControl* control, MftProgressCallback callback,
                                             void* context);
extern "C" void CloseMftDumpInput(MftDumpInput* input);
extern "C" void SetAllocFailCountdown(int countdown);
extern "C" void SetReadFailCountdown(int countdown);
extern "C" void SetMaxThreads(unsigned maxThreads);
extern "C" void ResetTestState();

namespace {

constexpr uint64_t kDefaultRecordCount = 1024;
constexpr uint32_t kDefaultBufferRecords = 4096;
constexpr const char* kFixturePath = "/tmp/mftlib_synthetic.mft";

bool generate_fixture() {
    return GenerateSyntheticMFTSizedUtf8(kFixturePath, kDefaultRecordCount, kDefaultBufferRecords, 1024);
}

void remove_fixture() { std::remove(kFixturePath); }

// True when a result's UTF-16 message is exactly the ASCII text expected.
bool message_is(const MftMessageChar* message, const char* expected) {
    size_t index = 0;
    while (expected[index] != '\0' && message[index] == static_cast<MftMessageChar>(expected[index])) {
        index++;
    }
    return expected[index] == '\0' && message[index] == 0;
}

// Opens, parses and closes one dump. Null when the open or the result allocation fails.
MftParseResult* parse_dump(const char* path, uint32_t bufferRecords, const MftParseControl* control = nullptr,
                           MftProgressCallback callback = nullptr, void* context = nullptr) {
    MftDumpInputInfo info{};
    MftDumpInput* input = OpenMftDumpInput(path, &info);
    if (input == nullptr) {
        return nullptr;
    }
    MftParseResult* parseResult = ParseMftDumpInput(input, bufferRecords, control, callback, context);
    CloseMftDumpInput(input);
    return parseResult;
}

// --- Tests ---

bool test_abi_version() {
    uint32_t abiVersion = GetMftNativeAbiVersion();
    if (abiVersion != MFT_NATIVE_ABI_VERSION) {
        std::fprintf(stderr, "  FAIL: GetMftNativeAbiVersion() returned %u, expected %u\n", abiVersion,
                     MFT_NATIVE_ABI_VERSION);
        return false;
    }
    return true;
}

bool test_round_trip() {
    if (!generate_fixture()) {
        std::fprintf(stderr, "  setup FAIL: GenerateSyntheticMFTSizedUtf8 returned false\n");
        return false;
    }
    MftParseResult* parseResult = parse_dump(kFixturePath, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 &&
                      parseResult->errorMessage[0] == L'\0' && parseResult->abiVersion == MFT_NATIVE_ABI_VERSION &&
                      parseResult->entryStride == 52 && parseResult->entries != nullptr &&
                      parseResult->entryStrings != nullptr &&
                      parseResult->entryStringUnits < parseResult->usedRecords * 260;
    if (testPassed) {
        // The fixture writes SequenceNumber = recordIndex + 1 and parent sequence 0 for every record.
        for (uint64_t index = 0; index < parseResult->usedRecords; ++index) {
            const MftCompactEntry& entry = parseResult->entries[index];
            if (entry.sequenceNumber != static_cast<uint16_t>(entry.recordNumber + 1) ||
                entry.parentSequenceNumber != 0) {
                std::fprintf(stderr, "  FAIL: record %llu sequenceNumber %u, expected %llu\n",
                             static_cast<unsigned long long>(entry.recordNumber),
                             static_cast<unsigned>(entry.sequenceNumber),
                             static_cast<unsigned long long>(entry.recordNumber + 1));
                testPassed = false;
            }
        }
    }
    if (testPassed) {
        std::printf("  total=%llu used=%llu stringUnits=%llu ioMs=%.2f parseMs=%.2f totalMs=%.2f\n",
                    static_cast<unsigned long long>(parseResult->totalRecords),
                    static_cast<unsigned long long>(parseResult->usedRecords),
                    static_cast<unsigned long long>(parseResult->entryStringUnits), parseResult->ioTimeMs,
                    parseResult->parseTimeMs, parseResult->totalTimeMs);
    } else if (parseResult != nullptr) {
        std::fprintf(stderr, "  FAIL: usedRecords=%llu abiVersion=%u entryStride=%u errorMessage[0]=%d\n",
                     static_cast<unsigned long long>(parseResult->usedRecords), parseResult->abiVersion,
                     parseResult->entryStride, static_cast<int>(parseResult->errorMessage[0]));
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    remove_fixture();
    return testPassed;
}

bool test_round_trip_4096() {
    constexpr const char* kFixture4096Path = "/tmp/mftlib_synthetic_4096.mft";
    if (!GenerateSyntheticMFTSizedUtf8(kFixture4096Path, kDefaultRecordCount, kDefaultBufferRecords, 4096)) {
        std::fprintf(stderr, "  setup FAIL: GenerateSyntheticMFTSizedUtf8(4096) returned false\n");
        return false;
    }
    MftParseResult* parseResult = parse_dump(kFixture4096Path, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 &&
                      parseResult->errorMessage[0] == L'\0' && parseResult->abiVersion == MFT_NATIVE_ABI_VERSION &&
                      parseResult->entryStride == 52;
    if (testPassed) {
        std::printf("  4096: total=%llu used=%llu ioMs=%.2f parseMs=%.2f totalMs=%.2f\n",
                    static_cast<unsigned long long>(parseResult->totalRecords),
                    static_cast<unsigned long long>(parseResult->usedRecords), parseResult->ioTimeMs,
                    parseResult->parseTimeMs, parseResult->totalTimeMs);
    } else if (parseResult != nullptr) {
        std::fprintf(stderr, "  FAIL: usedRecords=%llu errorMessage[0]=%d\n",
                     static_cast<unsigned long long>(parseResult->usedRecords),
                     static_cast<int>(parseResult->errorMessage[0]));
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixture4096Path);
    return testPassed;
}

bool test_parse_missing_file() {
    MftDumpInputInfo info{};
    MftDumpInput* input = OpenMftDumpInput("/tmp/does_not_exist_4f8e7c.mft", &info);
    const bool testPassed = input == nullptr && info.errorMessage[0] != 0 && info.invalidInput == 0 &&
                            info.lengthBytes == 0 && info.recordSize == 0;
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: expected the open to fail; got input=%p err[0]=%d\n", static_cast<void*>(input),
                     static_cast<int>(info.errorMessage[0]));
    }
    CloseMftDumpInput(input);
    return testPassed;
}

bool test_parse_empty_file() {
    const char* path = "/tmp/mftlib_empty.mft";
    FILE* fileHandle = std::fopen(path, "wb");
    if (fileHandle == nullptr) {
        return false;
    }
    std::fclose(fileHandle);

    MftDumpInputInfo info{};
    MftDumpInput* input = OpenMftDumpInput(path, &info);
    const bool testPassed = input == nullptr && info.invalidInput == 1 && info.lengthBytes == 0 &&
                            message_is(info.errorMessage, "The dump file is empty.");
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: empty file got input=%p invalidInput=%u\n", static_cast<void*>(input),
                     info.invalidInput);
    }
    CloseMftDumpInput(input);
    std::remove(path);
    return testPassed;
}

bool test_fixture_round_trip() {
    constexpr const char* kFixturePathName = "/tmp/mftlib_fixture.mft";
    if (!GenerateFixtureMFTUtf8(kFixturePathName)) {
        std::fprintf(stderr, "  setup FAIL: GenerateFixtureMFTUtf8 returned false\n");
        return false;
    }
    MftParseResult* parseResult = parse_dump(kFixturePathName, 4096);
    // Records 0 and 5 to 11 are in use and non-extension; 1 to 4 are zeroed,
    // and freed or malformed records follow. Only eight are emitted by default.
    bool passed = parseResult != nullptr && parseResult->errorMessage[0] == L'\0' &&
                  parseResult->totalRecords == kFixtureRecordCount && parseResult->usedRecords == 8;
    if (!passed && parseResult != nullptr) {
        std::fprintf(stderr, "  FAIL: total=%llu used=%llu\n",
                     static_cast<unsigned long long>(parseResult->totalRecords),
                     static_cast<unsigned long long>(parseResult->usedRecords));
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixturePathName);
    return passed;
}

bool test_fixture_modified_time() {
    constexpr const char* kFixturePathName = "/tmp/mftlib_fixture_time.mft";
    if (!GenerateFixtureMFTUtf8(kFixturePathName)) {
        return false;
    }
    MftParseResult* parseResult = parse_dump(kFixturePathName, 4096);
    bool passed = parseResult != nullptr && parseResult->entries != nullptr;
    if (passed) {
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
            const MftCompactEntry& entry = parseResult->entries[i];
            auto expected = static_cast<int64_t>(132000000000000000ULL + entry.recordNumber * 10000000ULL);
            if (entry.modifiedTime != expected) {
                std::fprintf(stderr, "  FAIL: record %llu modifiedTime %lld, expected %lld\n",
                             static_cast<unsigned long long>(entry.recordNumber),
                             static_cast<long long>(entry.modifiedTime), static_cast<long long>(expected));
                passed = false;
            }
        }
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixturePathName);
    return passed;
}

struct ExpectedSize {
    uint64_t recordNumber;
    int64_t size;
    bool sizeUnknown;
};

bool test_fixture_sizes() {
    constexpr const char* kFixturePathName = "/tmp/mftlib_fixture_sizes.mft";
    const std::array<ExpectedSize, 8> expected = {{
        {0, 65536, false},
        {5, 0, false},
        {6, 37, false},
        {7, 1234567, false},
        {8, 0, false},
        {9, 0, true},
        {10, 4096, false},
        {11, 0, true},
    }};
    if (!GenerateFixtureMFTUtf8(kFixturePathName)) {
        return false;
    }
    MftParseResult* parseResult = parse_dump(kFixturePathName, 4096);
    bool passed = parseResult != nullptr && parseResult->usedRecords == expected.size();
    if (passed) {
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
            const MftCompactEntry& entry = parseResult->entries[i];
            const ExpectedSize* match = nullptr;
            for (const auto& candidate : expected) {
                if (candidate.recordNumber == entry.recordNumber) {
                    match = &candidate;
                }
            }
            bool unknown = (entry.flags & MFT_ENTRY_FLAG_SIZE_UNKNOWN) != 0;
            if (match == nullptr || entry.size != match->size || unknown != match->sizeUnknown) {
                std::fprintf(stderr, "  FAIL: record %llu size %lld unknown %d\n",
                             static_cast<unsigned long long>(entry.recordNumber), static_cast<long long>(entry.size),
                             static_cast<int>(unknown));
                passed = false;
            }
        }
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixturePathName);
    return passed;
}

// Opens the synthetic fixture, then arms the allocation countdown so that the parse's
// allocationOrdinal-th allocation fails. Arming after the open keeps the open's own input
// allocation out of the count. Null when the fixture cannot be written or opened.
MftDumpInput* open_fixture_with_alloc_failure(int allocationOrdinal) {
    if (!generate_fixture()) {
        return nullptr;
    }
    MftDumpInputInfo info{};
    MftDumpInput* input = OpenMftDumpInput(kFixturePath, &info);
    if (input != nullptr) {
        SetAllocFailCountdown(allocationOrdinal);
    }
    return input;
}

// The parse's first allocation is its result, so failing it returns no result at all.
bool test_alloc_failure_path() {
    MftDumpInput* input = open_fixture_with_alloc_failure(1);
    if (input == nullptr) {
        remove_fixture();
        return false;
    }
    MftParseResult* parseResult = ParseMftDumpInput(input, kDefaultBufferRecords, nullptr, nullptr, nullptr);
    const bool testPassed = parseResult == nullptr;
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: result allocation failure still returned a result (used=%llu)\n",
                     static_cast<unsigned long long>(parseResult->usedRecords));
        FreeMftResult(parseResult);
    }
    CloseMftDumpInput(input);
    ResetTestState();
    remove_fixture();
    return testPassed;
}

// The parse allocates its result, two read buffers, the entry array, then the string pool:
// the fifth allocation.
bool test_string_pool_alloc_failure() {
    MftDumpInput* input = open_fixture_with_alloc_failure(5);
    if (input == nullptr) {
        remove_fixture();
        return false;
    }
    MftParseResult* parseResult = ParseMftDumpInput(input, kDefaultBufferRecords, nullptr, nullptr, nullptr);
    const bool testPassed = parseResult != nullptr && parseResult->usedRecords == 0 &&
                            message_is(parseResult->errorMessage, "Failed to allocate string pool");
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    CloseMftDumpInput(input);
    ResetTestState();
    remove_fixture();
    return testPassed;
}

bool test_read_failure_path() {
    if (!generate_fixture()) {
        return false;
    }
    SetReadFailCountdown(1);  // fail the next read
    MftParseResult* parseResult = parse_dump(kFixturePath, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords == 0 && parseResult->invalidInput == 1 &&
                      message_is(parseResult->errorMessage, "The dump file could not be read completely.");
    if (!testPassed && parseResult != nullptr) {
        std::fprintf(stderr, "  FAIL: read failure produced usedRecords=%llu invalidInput=%u\n",
                     static_cast<unsigned long long>(parseResult->usedRecords), parseResult->invalidInput);
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    SetReadFailCountdown(0);
    ResetTestState();
    remove_fixture();
    return testPassed;
}

bool test_generate_unwritable_path() {
    bool result = GenerateSyntheticMFTSizedUtf8("/tmp/this_dir_does_not_exist_abc123/output.mft", kDefaultRecordCount,
                                                kDefaultBufferRecords, 1024);
    bool testPassed = !result;
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: generate to unwritable path returned true\n");
    }
    return testPassed;
}

bool test_max_threads_clamping() {
    SetMaxThreads(1);
    if (!generate_fixture()) {
        ResetTestState();
        return false;
    }
    MftParseResult* parseResult = parse_dump(kFixturePath, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 &&
                      parseResult->errorMessage[0] == L'\0' && parseResult->entries != nullptr;
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    SetMaxThreads(0);
    ResetTestState();
    remove_fixture();
    return testPassed;
}

#include "linux_smoke_test.parse.cpp"
#include "linux_smoke_test.freed.cpp"
#include "linux_smoke_test.dump.cpp"

struct TestCase {
    const char* name;
    bool (*fn)();
};

}  // namespace

int main() {
    const std::array<TestCase, 25> tests = {{
        {"abi_version", test_abi_version},
        {"dump_excludes_freed", test_dump_excludes_freed},
        {"round_trip", test_round_trip},
        {"round_trip_4096", test_round_trip_4096},
        {"fixture_round_trip", test_fixture_round_trip},
        {"fixture_modified_time", test_fixture_modified_time},
        {"fixture_sizes", test_fixture_sizes},
        {"parse_missing_file", test_parse_missing_file},
        {"parse_empty_file", test_parse_empty_file},
        {"alloc_failure_path", test_alloc_failure_path},
        {"string_pool_alloc_failure", test_string_pool_alloc_failure},
        {"read_failure_path", test_read_failure_path},
        {"generate_unwritable_path", test_generate_unwritable_path},
        {"max_threads_clamping", test_max_threads_clamping},
        {"malformed_attribute_offset", test_malformed_attribute_offset},
        {"malformed_nonresident_data_length", test_malformed_nonresident_data_length},
        {"zero_length_file_name", test_zero_length_file_name},
        {"progress_callback", test_progress_callback},
        {"file_parse_control_block", test_file_parse_control_block},
        {"parallel_progress_monotonicity", test_parallel_progress_monotonicity},
        {"dump_input_round_trip", test_dump_input_round_trip},
        {"dump_input_rejections", test_dump_input_rejections},
        {"dump_input_hostile_fixups", test_dump_input_hostile_fixups},
        {"dump_input_changed_length", test_dump_input_changed_length},
        {"dump_input_null_arguments", test_dump_input_null_arguments},
    }};

    int passedCount = 0;
    int failedCount = 0;
    for (const auto& testCase : tests) {
        std::printf("[%s] running\n", testCase.name);
        if (testCase.fn()) {
            std::printf("[%s] PASS\n", testCase.name);
            passedCount++;
        } else {
            std::printf("[%s] FAIL\n", testCase.name);
            failedCount++;
        }
    }
    std::printf("\n=== %d passed, %d failed ===\n", passedCount, failedCount);
    return (failedCount == 0) ? 0 : 1;
}
