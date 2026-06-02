// mft-cli.cpp — small Linux CLI over libMFTLibNative.so for dumping and
// searching parsed $MFT files.
//
// Usage:
//   mft-cli dump   <mft-path>
//   mft-cli search <mft-path> <pattern>
//
// `dump` prints record/timing summary plus the first N filenames.
// `search` walks every used entry, case-insensitive ASCII substring match
// on the filename, and prints matches.
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#include "mft_api.h"

extern "C" MftParseResult* ParseMFTFromFileUtf8(const char* filePath, const wchar_t* filter, uint32_t matchFlags,
                                                uint32_t bufferSizeRecords);
extern "C" void FreeMftResult(MftParseResult* result);

namespace {

constexpr uint32_t kBufferSizeRecords = 4096;
constexpr uint64_t kDumpSampleCount = 25;

void print_usage(const char* prog) {
    std::fprintf(stderr,
                 "Usage:\n"
                 "  %s dump   <mft-path>             # summary + first %llu filenames\n"
                 "  %s search <mft-path> <pattern>   # case-insensitive substring on filename\n",
                 prog, (unsigned long long)kDumpSampleCount, prog);
}

// Encode a wchar_t buffer (codepoints, possibly zero-padded after the name)
// as UTF-8 for printing/comparison. Handles full Unicode range.
std::string wide_to_utf8(const wchar_t* w, size_t maxLen) {
    std::string out;
    out.reserve(maxLen);
    for (size_t i = 0; i < maxLen; i++) {
        uint32_t c = static_cast<uint32_t>(w[i]);
        if (c == 0) break;
        if (c < 0x80) {
            out.push_back(static_cast<char>(c));
        } else if (c < 0x800) {
            out.push_back(static_cast<char>(0xC0 | (c >> 6)));
            out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
        } else if (c < 0x10000) {
            out.push_back(static_cast<char>(0xE0 | (c >> 12)));
            out.push_back(static_cast<char>(0x80 | ((c >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
        } else if (c <= 0x10FFFF) {
            out.push_back(static_cast<char>(0xF0 | (c >> 18)));
            out.push_back(static_cast<char>(0x80 | ((c >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((c >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
        } else {
            out.push_back('?');
        }
    }
    return out;
}

bool icontains_ascii(const std::string& haystack, const std::string& needle) {
    if (needle.empty()) return true;
    auto cmp = [](char a, char b) {
        return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b));
    };
    return std::search(haystack.begin(), haystack.end(), needle.begin(), needle.end(), cmp) != haystack.end();
}

const char* type_marker(uint16_t flags) { return (flags & 0x2) ? "/" : ""; }

void print_entry(const MftFileEntry& e, const std::string& name) {
    std::printf("rec=%-8llu parent=%-8llu flags=0x%04x attr=0x%08x %s%s\n", (unsigned long long)e.recordNumber,
                (unsigned long long)e.parentRecordNumber, (unsigned)e.flags, (unsigned)e.fileAttributes, name.c_str(),
                type_marker(e.flags));
}

int do_dump(const MftParseResult* r) {
    uint64_t shown = 0;
    std::printf("First %llu filenames:\n", (unsigned long long)kDumpSampleCount);
    for (uint64_t i = 0; i < r->usedRecords && shown < kDumpSampleCount; i++) {
        const auto& e = r->entries[i];
        if (e.fileNameLength == 0) continue;
        std::string name = wide_to_utf8(e.fileName, e.fileNameLength);
        print_entry(e, name);
        shown++;
    }
    return 0;
}

int do_search(const MftParseResult* r, const std::string& pattern) {
    uint64_t hits = 0;
    for (uint64_t i = 0; i < r->usedRecords; i++) {
        const auto& e = r->entries[i];
        if (e.fileNameLength == 0) continue;
        std::string name = wide_to_utf8(e.fileName, e.fileNameLength);
        if (icontains_ascii(name, pattern)) {
            print_entry(e, name);
            hits++;
        }
    }
    std::printf("\n%llu match(es) for \"%s\"\n", (unsigned long long)hits, pattern.c_str());
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        print_usage(argv[0]);
        return 2;
    }
    std::string cmd = argv[1];
    const char* path = argv[2];
    std::string pattern;
    if (cmd == "search") {
        if (argc < 4) {
            print_usage(argv[0]);
            return 2;
        }
        pattern = argv[3];
    } else if (cmd != "dump") {
        print_usage(argv[0]);
        return 2;
    }

    std::printf("Parsing %s ...\n", path);
    MftParseResult* r = ParseMFTFromFileUtf8(path, nullptr, 0, kBufferSizeRecords);
    if (!r) {
        std::fprintf(stderr, "FAIL: parser returned null\n");
        return 1;
    }
    if (r->errorMessage[0] != 0) {
        std::string msg = wide_to_utf8(r->errorMessage, 256);
        std::fprintf(stderr, "FAIL: %s\n", msg.c_str());
        FreeMftResult(r);
        return 1;
    }

    std::printf("totalRecords=%llu usedRecords=%llu ioMs=%.2f parseMs=%.2f totalMs=%.2f\n\n",
                (unsigned long long)r->totalRecords, (unsigned long long)r->usedRecords, r->ioTimeMs, r->parseTimeMs,
                r->totalTimeMs);

    int rc = (cmd == "dump") ? do_dump(r) : do_search(r, pattern);
    FreeMftResult(r);
    return rc;
}
