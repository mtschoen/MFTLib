#include "pch.h"

#include "platform.h"
#include "../internal.h"

#include <fileapi.h>
#include <handleapi.h>
#include <stringapiset.h>

#include <algorithm>
#include <cassert>
#include <string>

namespace mftlib::platform {
struct File {
    HANDLE handle;
};

namespace {
std::wstring utf8_to_wide(const char* utf8) {
    if (utf8 == nullptr) {
        return {};
    }
    const int wideLength = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    std::wstring wide(static_cast<size_t>((std::max)(wideLength - 1, 0)), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, wide.data(), wideLength);
    return wide;
}
}  // namespace

File* open_read(const char* path_utf8) {
    const auto wide = utf8_to_wide(path_utf8);
    const HANDLE handle =
        CreateFileW(wide.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                    OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return nullptr;
    }
    auto* file = new (std::nothrow) File{handle};
    if (file == nullptr) {
        CloseHandle(handle);
    }
    return file;
}

File* open_write(const char* path_utf8) {
    const auto wide = utf8_to_wide(path_utf8);
    const HANDLE handle = CreateFileW(wide.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return nullptr;
    }
    auto* file = new (std::nothrow) File{handle};
    if (file == nullptr) {
        CloseHandle(handle);
    }
    return file;
}

int64_t size_of(const File* file) {
    assert(file != nullptr);
    LARGE_INTEGER sizeInfo{};
    if (ShouldFailFileSize() || GetFileSizeEx(file->handle, &sizeInfo) == 0) {
        return -1;
    }
    return sizeInfo.QuadPart;
}

int64_t pread_at(const File* file, void* buffer, const size_t count, const FileOffset offset) {
    assert(file != nullptr);
    OVERLAPPED overlapped{};
    overlapped.Offset = static_cast<DWORD>(offset.value & 0xFFFFFFFF);
    overlapped.OffsetHigh = static_cast<DWORD>((offset.value >> 32) & 0xFFFFFFFF);
    DWORD bytesRead = 0;
    BOOL readOk = ReadFile(file->handle, buffer, static_cast<DWORD>(count), &bytesRead, &overlapped);
    if (ShouldFailPlatformRead()) {
        readOk = FALSE;
        SetLastError(ERROR_ACCESS_DENIED);
    }
    if (readOk == 0) {
        if (const DWORD errorCode = GetLastError(); errorCode != ERROR_HANDLE_EOF) {
            return -1;
        }
    }
    return bytesRead;
}

int64_t pwrite_at(const File* file, const void* buffer, const size_t count, const FileOffset offset) {
    assert(file != nullptr);
    OVERLAPPED overlapped{};
    overlapped.Offset = static_cast<DWORD>(offset.value & 0xFFFFFFFF);
    overlapped.OffsetHigh = static_cast<DWORD>((offset.value >> 32) & 0xFFFFFFFF);
    DWORD bytesWritten = 0;
    BOOL writeOk = WriteFile(file->handle, buffer, static_cast<DWORD>(count), &bytesWritten, &overlapped);
    if (ShouldFailPlatformWrite()) {
        writeOk = FALSE;
    }
    if (writeOk == 0) {
        return -1;
    }
    return bytesWritten;
}

void close_file(File* file) {
    if (file == nullptr || file->handle == INVALID_HANDLE_VALUE) {
        return;
    }
    const std::unique_ptr<File> owned(file);
    CloseHandle(owned->handle);
}

void* big_allocate(const size_t bytes) {
    return VirtualAlloc(nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
}

void big_free(void* pointer, size_t /*bytes*/) {
    if (pointer != nullptr) {
        VirtualFree(pointer, 0, MEM_RELEASE);
    }
}

uint32_t last_error() { return static_cast<uint32_t>(GetLastError()); }
}  // namespace mftlib::platform
