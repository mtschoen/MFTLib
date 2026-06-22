#include "pch.h"

#include "platform.h"
#include "../internal.h"

#include <fileapi.h>
#include <handleapi.h>
#include <stringapiset.h>

#include <string>

namespace mftlib::platform {

struct File {
    HANDLE h;
};

namespace {
std::wstring utf8_to_wide(const char* s) {
    if (s == nullptr) {
        return {};
    }
    int wlen = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (wlen <= 0) {
        return {};
    }
    std::wstring w(static_cast<size_t>(wlen - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), wlen);
    return w;
}
}  // namespace

File* open_read(const char* path_utf8) {
    auto wide = utf8_to_wide(path_utf8);
    HANDLE h = CreateFileW(wide.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return nullptr;
    }
    return new File{h};
}

File* open_write(const char* path_utf8) {
    auto wide = utf8_to_wide(path_utf8);
    HANDLE h = CreateFileW(wide.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL,
                           nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return nullptr;
    }
    return new File{h};
}

int64_t size_of(const File* f) {
    if (f == nullptr) {
        return -1;
    }
    LARGE_INTEGER li{};
    if (GetFileSizeEx(f->h, &li) == 0) {
        return -1;
    }
    return li.QuadPart;
}

int64_t pread_at(const File* f, void* buf, size_t count, int64_t offset) {
    if (f == nullptr) {
        return -1;
    }
    OVERLAPPED ov{};
    ov.Offset = static_cast<DWORD>(offset & 0xFFFFFFFF);
    ov.OffsetHigh = static_cast<DWORD>((offset >> 32) & 0xFFFFFFFF);
    DWORD bytesRead = 0;
    BOOL readOk = ReadFile(f->h, buf, static_cast<DWORD>(count), &bytesRead, &ov);
    if (ShouldFailPlatformRead()) {
        readOk = FALSE;
        SetLastError(ERROR_ACCESS_DENIED);
    }
    if (readOk == 0) {
        DWORD err = GetLastError();
        if (err != ERROR_HANDLE_EOF) {
            return -1;
        }
    }
    return bytesRead;
}

int64_t pwrite_at(const File* f, const void* buf, size_t count, int64_t offset) {
    if (f == nullptr) {
        return -1;
    }
    OVERLAPPED ov{};
    ov.Offset = static_cast<DWORD>(offset & 0xFFFFFFFF);
    ov.OffsetHigh = static_cast<DWORD>((offset >> 32) & 0xFFFFFFFF);
    DWORD bytesWritten = 0;
    BOOL writeOk = WriteFile(f->h, buf, static_cast<DWORD>(count), &bytesWritten, &ov);
    if (ShouldFailPlatformWrite()) {
        writeOk = FALSE;
    }
    if (writeOk == 0) {
        return -1;
    }
    return bytesWritten;
}

void close_file(File* f) {
    std::unique_ptr<File> owned(f);
    if (owned == nullptr) {
        return;
    }
    if (owned->h != INVALID_HANDLE_VALUE) {
        CloseHandle(owned->h);
    }
}

void* big_alloc(size_t bytes) { return VirtualAlloc(nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE); }

void big_free(void* ptr, size_t /*bytes*/) {
    if (ptr != nullptr) {
        VirtualFree(ptr, 0, MEM_RELEASE);
    }
}

uint32_t last_error() { return static_cast<uint32_t>(GetLastError()); }

}  // namespace mftlib::platform
