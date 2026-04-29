// linux_smoke_test.cpp — native end-to-end + error-path tests on POSIX.
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <sys/stat.h>
#include <unistd.h>

#include "mft_api.h"

extern "C" bool GenerateSyntheticMFTUtf8(const char* filePath,
                                          uint64_t recordCount,
                                          uint32_t bufferSizeRecords);
extern "C" MftParseResult* ParseMFTFromFileUtf8(const char* filePath,
                                                 const wchar_t* filter,
                                                 uint32_t matchFlags,
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

bool generate_fixture() {
    return GenerateSyntheticMFTUtf8(kFixturePath, kDefaultRecordCount, kDefaultBufferRecords);
}

void remove_fixture() {
    std::remove(kFixturePath);
}

// --- Tests ---

bool test_round_trip() {
    if (!generate_fixture()) {
        std::fprintf(stderr, "  setup FAIL: GenerateSyntheticMFTUtf8 returned false\n");
        return false;
    }
    MftParseResult* r = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0, kDefaultBufferRecords);
    bool ok = r && r->usedRecords > 0 && r->errorMessage[0] == L'\0';
    if (ok) {
        std::printf("  total=%llu used=%llu ioMs=%.2f parseMs=%.2f totalMs=%.2f\n",
                    (unsigned long long)r->totalRecords,
                    (unsigned long long)r->usedRecords,
                    r->ioTimeMs, r->parseTimeMs, r->totalTimeMs);
    } else if (r) {
        std::fprintf(stderr, "  FAIL: usedRecords=%llu errorMessage[0]=%d\n",
                     (unsigned long long)r->usedRecords, (int)r->errorMessage[0]);
    }
    if (r) FreeMftResult(r);
    remove_fixture();
    return ok;
}

bool test_parse_missing_file() {
    MftParseResult* r = ParseMFTFromFileUtf8("/tmp/does_not_exist_4f8e7c.mft",
                                              nullptr, 0, kDefaultBufferRecords);
    bool ok = r && r->errorMessage[0] != L'\0' && r->usedRecords == 0;
    if (!ok) {
        std::fprintf(stderr, "  FAIL: expected errorMessage set; got result=%p err[0]=%d\n",
                     (void*)r, r ? (int)r->errorMessage[0] : -1);
    }
    if (r) FreeMftResult(r);
    return ok;
}

bool test_parse_empty_file() {
    const char* path = "/tmp/mftlib_empty.mft";
    FILE* f = std::fopen(path, "wb");
    if (!f) return false;
    std::fclose(f);

    MftParseResult* r = ParseMFTFromFileUtf8(path, nullptr, 0, kDefaultBufferRecords);
    // Empty file → zero records → either an error or a result with totalRecords==0
    bool ok = r && r->totalRecords == 0;
    if (!ok && r) {
        std::fprintf(stderr, "  FAIL: empty file got totalRecords=%llu\n",
                     (unsigned long long)r->totalRecords);
    }
    if (r) FreeMftResult(r);
    std::remove(path);
    return ok;
}

bool test_parse_filter_returns_error() {
    if (!generate_fixture()) return false;
    // On Linux, passing a non-null filter must return an error result rather than
    // silently producing wrong matches (filter logic is Windows-only for now).
    MftParseResult* r = ParseMFTFromFileUtf8(kFixturePath, L"file_*", 2,
                                              kDefaultBufferRecords);
    bool ok = r && r->errorMessage[0] != L'\0';
    if (!ok && r) {
        std::fprintf(stderr, "  FAIL: expected errorMessage set, got empty (used=%llu)\n",
                     (unsigned long long)r->usedRecords);
    }
    if (r) FreeMftResult(r);
    remove_fixture();
    return ok;
}

bool test_alloc_failure_path() {
    if (!generate_fixture()) return false;
    SetAllocFailCountdown(1);  // fail the next allocation in the parse path
    MftParseResult* r = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0,
                                              kDefaultBufferRecords);
    // Should either return null or a result with an error message; usedRecords likely 0.
    bool ok = !r || r->errorMessage[0] != L'\0' || r->usedRecords == 0;
    if (!ok) {
        std::fprintf(stderr, "  FAIL: alloc failure didn't propagate (used=%llu err[0]=%d)\n",
                     (unsigned long long)r->usedRecords, (int)r->errorMessage[0]);
    }
    if (r) FreeMftResult(r);
    SetAllocFailCountdown(0);  // disarm
    ResetTestState();
    remove_fixture();
    return ok;
}

bool test_read_failure_path() {
    if (!generate_fixture()) return false;
    SetReadFailCountdown(1);  // fail the next read
    MftParseResult* r = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0,
                                              kDefaultBufferRecords);
    // The read failure should result in zero records actually parsed.
    bool ok = !r || r->usedRecords == 0;
    if (!ok) {
        std::fprintf(stderr, "  FAIL: read failure produced usedRecords=%llu\n",
                     (unsigned long long)r->usedRecords);
    }
    if (r) FreeMftResult(r);
    SetReadFailCountdown(0);
    ResetTestState();
    remove_fixture();
    return ok;
}

bool test_generate_unwritable_path() {
    // Path that contains a directory that doesn't exist → open_write fails → returns false.
    bool result = GenerateSyntheticMFTUtf8(
        "/tmp/this_dir_does_not_exist_abc123/output.mft",
        kDefaultRecordCount, kDefaultBufferRecords);
    bool ok = !result;  // expect false
    if (!ok) {
        std::fprintf(stderr, "  FAIL: generate to unwritable path returned true\n");
    }
    return ok;
}

bool test_max_threads_clamping() {
    // Constrain to 1 thread to exercise the single-threaded code path in EffectiveThreadCount.
    SetMaxThreads(1);
    if (!generate_fixture()) {
        ResetTestState();
        return false;
    }
    MftParseResult* r = ParseMFTFromFileUtf8(kFixturePath, nullptr, 0,
                                              kDefaultBufferRecords);
    bool ok = r && r->usedRecords > 0 && r->errorMessage[0] == L'\0';
    if (r) FreeMftResult(r);
    SetMaxThreads(0);
    ResetTestState();
    remove_fixture();
    return ok;
}

struct TestCase {
    const char* name;
    bool (*fn)();
};

}  // namespace

int main() {
    const TestCase tests[] = {
        {"round_trip",                test_round_trip},
        {"parse_missing_file",        test_parse_missing_file},
        {"parse_empty_file",          test_parse_empty_file},
        {"parse_filter_returns_error", test_parse_filter_returns_error},
        {"alloc_failure_path",        test_alloc_failure_path},
        {"read_failure_path",         test_read_failure_path},
        {"generate_unwritable_path",  test_generate_unwritable_path},
        {"max_threads_clamping",      test_max_threads_clamping},
    };

    int passed = 0, failed = 0;
    for (const auto& t : tests) {
        std::printf("[%s] running\n", t.name);
        if (t.fn()) {
            std::printf("[%s] PASS\n", t.name);
            passed++;
        } else {
            std::printf("[%s] FAIL\n", t.name);
            failed++;
        }
    }
    std::printf("\n=== %d passed, %d failed ===\n", passed, failed);
    return failed == 0 ? 0 : 1;
}
