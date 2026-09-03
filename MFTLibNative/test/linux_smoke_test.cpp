// linux_smoke_test.cpp - native end-to-end + error-path tests on POSIX.
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

#include "mft_api.h"

extern "C" bool GenerateSyntheticMFTUtf8(const char* filePath, uint64_t recordCount, uint32_t bufferSizeRecords);
extern "C" bool GenerateSyntheticMFTSizedUtf8(const char* filePath, uint64_t recordCount, uint32_t bufferSizeRecords,
                                              uint32_t recordSize);
extern "C" bool GenerateFixtureMFTUtf8(const char* filePath);
extern "C" MftParseResult* ParseMFTFromFileUtf8(const char* filePath, const wchar_t* filter, uint32_t matchFlags,
                                                uint32_t bufferSizeRecords);
extern "C" MftParseResult* ParseMFTFromFileUtf8WithProgress(const char* filePath, const wchar_t* filter,
                                                            uint32_t matchFlags, uint32_t bufferSizeRecords,
                                                            MftProgressCallback callback, void* context);
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

bool test_abi_version() {
    uint32_t abiVersion = GetMftNativeAbiVersion();
    if (abiVersion != 4) {
        std::fprintf(stderr, "  FAIL: GetMftNativeAbiVersion() returned %u, expected 4\n", abiVersion);
        return false;
    }
    return true;
}

bool test_round_trip() {
    if (!generate_fixture()) {
        std::fprintf(stderr, "  setup FAIL: GenerateSyntheticMFTUtf8 returned false\n");
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
    bool testPassed =
        (parseResult != nullptr) && parseResult->usedRecords > 0 && parseResult->errorMessage[0] == L'\0' &&
        parseResult->abiVersion == 4 && parseResult->entryStride == 48 && parseResult->entries != nullptr &&
        parseResult->entryStrings != nullptr && parseResult->entryStringUnits < parseResult->usedRecords * 260;
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
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixture4096Path, nullptr, 0, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->usedRecords > 0 &&
                      parseResult->errorMessage[0] == L'\0' && parseResult->abiVersion == 4 &&
                      parseResult->entryStride == 48;
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
    bool testPassed = (parseResult != nullptr) && parseResult->errorMessage[0] != L'\0' &&
                      parseResult->usedRecords == 0 && parseResult->abiVersion == 4 && parseResult->entryStride == 48;
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
    bool testPassed = (parseResult != nullptr) && parseResult->totalRecords == 0 && parseResult->abiVersion == 4 &&
                      parseResult->entryStride == 48;
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

bool test_fixture_round_trip() {
    constexpr const char* kFixturePathName = "/tmp/mftlib_fixture.mft";
    if (!GenerateFixtureMFTUtf8(kFixturePathName)) {
        std::fprintf(stderr, "  setup FAIL: GenerateFixtureMFTUtf8 returned false\n");
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePathName, nullptr, 0, 4096);
    // Records 0, 5, 6, 7, 8, 9, and 10 are in use and non-extension; 1 to 4 and 11 are
    // zeroed, so the parser reports twelve total and seven used.
    bool passed = parseResult != nullptr && parseResult->errorMessage[0] == L'\0' &&
                  parseResult->totalRecords == 12 && parseResult->usedRecords == 7;
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

bool test_alloc_failure_path() {
    if (!generate_fixture()) {
        return false;
    }
    SetAllocFailCountdown(1);  // fail the next allocation in the parse path
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
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

bool test_string_pool_alloc_failure() {
    if (!generate_fixture()) {
        return false;
    }
    // Fail allocation on string pool allocation (countdown = 4 in AllocateParseBuffers)
    SetAllocFailCountdown(4);
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
    bool testPassed = (parseResult != nullptr) && parseResult->errorMessage[0] != L'\0';
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    SetAllocFailCountdown(0);
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
    bool result = GenerateSyntheticMFTUtf8("/tmp/this_dir_does_not_exist_abc123/output.mft", kDefaultRecordCount,
                                           kDefaultBufferRecords);
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
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
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
    if (parseResult != nullptr && parseResult->pathEntries != nullptr) {
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
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
                      parseResult->entryStringUnits == 0 && hasRootEntry;
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }

    // Path allocation failure fallback: fail the pathEntries allocation
    // Count of allocations before path allocation: result(1) + lookup(2) + buf0(3) + buf1(4) + entries(5) + strings(6)
    // -> paths.entries is 7
    SetAllocFailCountdown(7);
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

bool test_progress_callback() {
    if (!generate_fixture()) {
        return false;
    }
    std::vector<ProgressReport> reports;
    auto callback = [](MftScanPhase phase, uint64_t recordsScanned, uint64_t totalRecords, double elapsedMs,
                       void* context) {
        auto* vec = static_cast<std::vector<ProgressReport>*>(context);
        vec->push_back({phase, recordsScanned, totalRecords, elapsedMs});
    };

    MftParseResult* result =
        ParseMFTFromFileUtf8WithProgress(kFixturePath, nullptr, MATCH_FLAG_RESOLVE_PATHS, 1, callback, &reports);
    bool ok = (result != nullptr && result->usedRecords > 0);
    if (ok) {
        if (reports.empty()) {
            std::fprintf(stderr, "  FAIL: no progress reports\n");
            ok = false;
        } else {
            bool sawParsing = false;
            bool sawResolving = false;
            uint64_t prevParsing = 0;
            uint64_t prevResolving = 0;
            for (const auto& r : reports) {
                if (r.phase == MftScanPhase::Parsing) {
                    sawParsing = true;
                    if (r.recordsScanned <= prevParsing || r.recordsScanned > r.totalRecords) {
                        std::fprintf(stderr, "  FAIL: parsing progress not monotonic (prev=%llu cur=%llu total=%llu)\n",
                                     static_cast<unsigned long long>(prevParsing),
                                     static_cast<unsigned long long>(r.recordsScanned),
                                     static_cast<unsigned long long>(r.totalRecords));
                        ok = false;
                        break;
                    }
                    prevParsing = r.recordsScanned;
                } else if (r.phase == MftScanPhase::ResolvingPaths) {
                    sawResolving = true;
                    if (r.recordsScanned < prevResolving || r.recordsScanned > r.totalRecords) {
                        std::fprintf(stderr,
                                     "  FAIL: resolving progress not monotonic (prev=%llu cur=%llu total=%llu)\n",
                                     static_cast<unsigned long long>(prevResolving),
                                     static_cast<unsigned long long>(r.recordsScanned),
                                     static_cast<unsigned long long>(r.totalRecords));
                        ok = false;
                        break;
                    }
                    prevResolving = r.recordsScanned;
                }
            }
            if (ok && !sawParsing) {
                std::fprintf(stderr, "  FAIL: no Parsing phase reports seen\n");
                ok = false;
            }
            if (ok && !sawResolving) {
                std::fprintf(stderr, "  FAIL: no ResolvingPaths phase reports seen\n");
                ok = false;
            }
            if (ok && prevParsing != result->totalRecords) {
                std::fprintf(stderr, "  FAIL: final parsing report (%llu) != totalRecords (%llu)\n",
                             static_cast<unsigned long long>(prevParsing),
                             static_cast<unsigned long long>(result->totalRecords));
                ok = false;
            }
            if (ok && prevResolving != result->usedRecords) {
                std::fprintf(stderr, "  FAIL: final resolving report (%llu) != usedRecords (%llu)\n",
                             static_cast<unsigned long long>(prevResolving),
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

bool test_parallel_progress_monotonicity() {
    constexpr const char* kFixtureParallel = "/tmp/mftlib_parallel_progress.mft";
    constexpr uint64_t kRecordCount = 70000;
    if (!GenerateSyntheticMFTSizedUtf8(kFixtureParallel, kRecordCount, 4096, 1024)) {
        return false;
    }
    SetMaxThreads(8);
    std::vector<ProgressReport> reports;
    auto callback = [](MftScanPhase phase, uint64_t recordsScanned, uint64_t totalRecords, double elapsedMs,
                       void* context) {
        auto* vec = static_cast<std::vector<ProgressReport>*>(context);
        vec->push_back({phase, recordsScanned, totalRecords, elapsedMs});
    };

    MftParseResult* result =
        ParseMFTFromFileUtf8WithProgress(kFixtureParallel, nullptr, MATCH_FLAG_RESOLVE_PATHS, 4096, callback, &reports);
    SetMaxThreads(0);
    ResetTestState();

    bool ok = (result != nullptr && result->usedRecords > 0 && !reports.empty());
    if (ok) {
        uint64_t prevParsing = 0;
        uint64_t prevResolving = 0;
        uint64_t resolvingReportCount = 0;
        bool sawResolving = false;
        for (const auto& r : reports) {
            if (r.phase == MftScanPhase::Parsing) {
                if (r.recordsScanned < prevParsing || r.recordsScanned > r.totalRecords) {
                    std::fprintf(stderr, "  FAIL: parallel parsing progress not monotonic (prev=%llu cur=%llu total=%llu)\n",
                                 static_cast<unsigned long long>(prevParsing),
                                 static_cast<unsigned long long>(r.recordsScanned),
                                 static_cast<unsigned long long>(r.totalRecords));
                    ok = false;
                    break;
                }
                prevParsing = r.recordsScanned;
            } else if (r.phase == MftScanPhase::ResolvingPaths) {
                sawResolving = true;
                resolvingReportCount++;
                if (r.recordsScanned < prevResolving || r.recordsScanned > r.totalRecords) {
                    std::fprintf(stderr,
                                 "  FAIL: parallel resolving progress not monotonic (prev=%llu cur=%llu total=%llu)\n",
                                 static_cast<unsigned long long>(prevResolving),
                                 static_cast<unsigned long long>(r.recordsScanned),
                                 static_cast<unsigned long long>(r.totalRecords));
                    ok = false;
                    break;
                }
                prevResolving = r.recordsScanned;
            }
        }
        if (ok && !sawResolving) {
            std::fprintf(stderr, "  FAIL: no parallel ResolvingPaths phase reports seen\n");
            ok = false;
        }
        if (ok && resolvingReportCount < 16) {
            std::fprintf(stderr, "  FAIL: too few parallel resolving reports (%llu, expected at least 16)\n",
                         static_cast<unsigned long long>(resolvingReportCount));
            ok = false;
        }
    }
    if (result != nullptr) {
        FreeMftResult(result);
    }
    std::remove(kFixtureParallel);
    return ok;
}

struct TestCase {
    const char* name;
    bool (*fn)();
};

}  // namespace

int main() {
    const std::array<TestCase, 17> tests = {{
        {"abi_version", test_abi_version},
        {"round_trip", test_round_trip},
        {"round_trip_4096", test_round_trip_4096},
        {"fixture_round_trip", test_fixture_round_trip},
        {"parse_missing_file", test_parse_missing_file},
        {"parse_empty_file", test_parse_empty_file},
        {"parse_filter_returns_error", test_parse_filter_returns_error},
        {"alloc_failure_path", test_alloc_failure_path},
        {"string_pool_alloc_failure", test_string_pool_alloc_failure},
        {"read_failure_path", test_read_failure_path},
        {"generate_unwritable_path", test_generate_unwritable_path},
        {"max_threads_clamping", test_max_threads_clamping},
        {"malformed_attribute_offset", test_malformed_attribute_offset},
        {"zero_length_file_name", test_zero_length_file_name},
        {"path_resolution_and_fallback", test_path_resolution_and_fallback},
        {"progress_callback", test_progress_callback},
        {"parallel_progress_monotonicity", test_parallel_progress_monotonicity},
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
