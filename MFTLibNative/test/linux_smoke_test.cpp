// linux_smoke_test.cpp — native end-to-end + error-path tests on POSIX.
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <sys/stat.h>
#include <unistd.h>

#include "mft_api.h"

extern "C" bool GenerateSyntheticMFTUtf8(const char* filePath, uint64_t recordCount, uint32_t bufferSizeRecords);
extern "C" bool GenerateSyntheticMFTSizedUtf8(const char* filePath, uint64_t recordCount, uint32_t bufferSizeRecords,
                                              uint32_t recordSize);
extern "C" MftParseResult* ParseMFTFromFileUtf8(const char* filePath, const wchar_t* filter, uint32_t matchFlags,
                                                uint32_t bufferSizeRecords);
extern "C" void FreeMftResult(MftParseResult* result);
extern "C" void SetAllocFailCountdown(int countdown);
extern "C" void SetReadFailCountdown(int countdown);
extern "C" void SetMaxThreads(unsigned maxThreads);
extern "C" void ResetTestState();

namespace {

constexpr uint64_t kDefaultRecordCount = 1024;
constexpr uint32_t kDefaultBufferRecords = 4096;
constexpr const char* kFixturePath = "/tmp/mftlib_synthetic.mft";

bool generate_fixture() { return GenerateSyntheticMFTUtf8(kFixturePath, kDefaultRecordCount, kDefaultBufferRecords); }

void remove_fixture() { std::remove(kFixturePath); }

// --- Tests ---

bool test_round_trip() {
    if (!generate_fixture()) {
        std::fprintf(stderr, "  setup FAIL: GenerateSyntheticMFTUtf8 returned false\n");
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 && parseResult->errorMessage[0] == L'\0';
    if (testPassed) {
        std::printf("  total=%llu used=%llu ioMs=%.2f parseMs=%.2f totalMs=%.2f\n",
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
    remove_fixture();
    return testPassed;
}

bool test_round_trip_4096() {
    constexpr const char* kFixture4096Path = "/tmp/mftlib_synthetic_4096.mft";
    if (!GenerateSyntheticMFTSizedUtf8(kFixture4096Path, kDefaultRecordCount, kDefaultBufferRecords, 4096)) {
        std::fprintf(stderr, "  setup FAIL: GenerateSyntheticMFTSizedUtf8(4096) returned false\n");
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixture4096Path, nullptr, 0, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 && parseResult->errorMessage[0] == L'\0';
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
    MftParseResult* parseResult =
        ParseMFTFromFileUtf8("/tmp/does_not_exist_4f8e7c.mft", nullptr, 0, kDefaultBufferRecords);
    bool testPassed =
        (parseResult != nullptr) && parseResult->errorMessage[0] != L'\0' && parseResult->usedRecords == 0;
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: expected errorMessage set; got result=%p err[0]=%d\n",
                     static_cast<void*>(parseResult),
                     (parseResult != nullptr) ? static_cast<int>(parseResult->errorMessage[0]) : -1);
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    return testPassed;
}

bool test_parse_empty_file() {
    const char* path = "/tmp/mftlib_empty.mft";
    FILE* fileHandle = std::fopen(path, "wb");
    if (fileHandle == nullptr) {
        return false;
    }
    std::fclose(fileHandle);

    MftParseResult* parseResult = ParseMFTFromFileUtf8(path, nullptr, 0, kDefaultBufferRecords);
    // Empty file → zero records → either an error or a result with totalRecords==0
    bool testPassed = (parseResult != nullptr) && parseResult->totalRecords == 0;
    if (!testPassed && parseResult != nullptr) {
        std::fprintf(stderr, "  FAIL: empty file got totalRecords=%llu\n",
                     static_cast<unsigned long long>(parseResult->totalRecords));
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(path);
    return testPassed;
}

bool test_parse_filter_returns_error() {
    if (!generate_fixture()) {
        return false;
    }
    // On Linux, passing a non-null filter must return an error result rather than
    // silently producing wrong matches (filter logic is Windows-only for now).
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, L"file_*", 2, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->errorMessage[0] != L'\0';
    if (!testPassed && parseResult != nullptr) {
        std::fprintf(stderr, "  FAIL: expected errorMessage set, got empty (used=%llu)\n",
                     static_cast<unsigned long long>(parseResult->usedRecords));
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    remove_fixture();
    return testPassed;
}

bool test_alloc_failure_path() {
    if (!generate_fixture()) {
        return false;
    }
    SetAllocFailCountdown(1);  // fail the next allocation in the parse path
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
    // Should either return null or a result with an error message; usedRecords likely 0.
    bool testPassed =
        (parseResult == nullptr) || parseResult->errorMessage[0] != L'\0' || parseResult->usedRecords == 0;
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: alloc failure didn't propagate (used=%llu err[0]=%d)\n",
                     static_cast<unsigned long long>(parseResult->usedRecords),
                     static_cast<int>(parseResult->errorMessage[0]));
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    SetAllocFailCountdown(0);  // disarm
    ResetTestState();
    remove_fixture();
    return testPassed;
}

bool test_read_failure_path() {
    if (!generate_fixture()) {
        return false;
    }
    SetReadFailCountdown(1);  // fail the next read
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
    // The read failure should result in zero records actually parsed.
    bool testPassed = (parseResult == nullptr) || parseResult->usedRecords == 0;
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: read failure produced usedRecords=%llu\n",
                     static_cast<unsigned long long>(parseResult->usedRecords));
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
    // Path that contains a directory that doesn't exist → open_write fails → returns false.
    bool result = GenerateSyntheticMFTUtf8("/tmp/this_dir_does_not_exist_abc123/output.mft", kDefaultRecordCount,
                                           kDefaultBufferRecords);
    bool testPassed = !result;  // expect false
    if (!testPassed) {
        std::fprintf(stderr, "  FAIL: generate to unwritable path returned true\n");
    }
    return testPassed;
}

bool test_max_threads_clamping() {
    // Constrain to 1 thread to exercise the single-threaded code path in EffectiveThreadCount.
    SetMaxThreads(1);
    if (!generate_fixture()) {
        ResetTestState();
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 && parseResult->errorMessage[0] == L'\0';
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    SetMaxThreads(0);
    ResetTestState();
    remove_fixture();
    return testPassed;
}

bool test_malformed_attribute_offset() {
    constexpr const char* kFixtureMalformedPath = "/tmp/mftlib_malformed_attr.mft";
    if (!GenerateSyntheticMFTSizedUtf8(kFixtureMalformedPath, 20, 256, 1024)) {
        return false;
    }
    // Tamper with record 6's StandardInformation attribute: set Resident ValueOffset = 60000 (0xEA60)
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

struct TestCase {
    const char* name;
    bool (*fn)();
};

}  // namespace

int main() {
    const std::array<TestCase, 10> tests = {{
        {"round_trip", test_round_trip},
        {"round_trip_4096", test_round_trip_4096},
        {"parse_missing_file", test_parse_missing_file},
        {"parse_empty_file", test_parse_empty_file},
        {"parse_filter_returns_error", test_parse_filter_returns_error},
        {"alloc_failure_path", test_alloc_failure_path},
        {"read_failure_path", test_read_failure_path},
        {"generate_unwritable_path", test_generate_unwritable_path},
        {"max_threads_clamping", test_max_threads_clamping},
        {"malformed_attribute_offset", test_malformed_attribute_offset},
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
