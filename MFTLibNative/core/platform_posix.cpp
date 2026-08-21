#include "platform.h"

#include <fcntl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <memory>

namespace mftlib::platform {

struct File {
    int fd;
};

File* open_read(const char* path_utf8) {
    if (path_utf8 == nullptr) {
        return nullptr;
    }
    int fileDescriptor = ::open(path_utf8, O_RDONLY | O_CLOEXEC);
    if (fileDescriptor < 0) {
        return nullptr;
    }
    posix_fadvise(fileDescriptor, 0, 0, POSIX_FADV_SEQUENTIAL);
    return new File{fileDescriptor};
}

File* open_write(const char* path_utf8) {
    if (path_utf8 == nullptr) {
        return nullptr;
    }
    int fileDescriptor = ::open(path_utf8, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
    if (fileDescriptor < 0) {
        return nullptr;
    }
    return new File{fileDescriptor};
}

int64_t size_of(const File* file) {
    if (file == nullptr) {
        return -1;
    }
    struct stat fileStat{};
    if (::fstat(file->fd, &fileStat) != 0) {
        return -1;
    }
    return static_cast<int64_t>(fileStat.st_size);
}

int64_t pread_at(const File* file, void* buf, size_t count, FileOffset offset) {
    if (file == nullptr) {
        return -1;
    }
    ssize_t totalBytes = 0;
    auto* bytePtr = static_cast<char*>(buf);
    int64_t currentOffset = offset.value;
    while (count > 0) {
        ssize_t bytesRead = ::pread(file->fd, bytePtr, count, currentOffset);
        if (bytesRead < 0) {
            return -1;
        }
        if (bytesRead == 0) {
            break;  // EOF
        }
        totalBytes += bytesRead;
        bytePtr += bytesRead;
        currentOffset += bytesRead;
        count -= static_cast<size_t>(bytesRead);
    }
    return totalBytes;
}

int64_t pwrite_at(const File* file, const void* buf, size_t count, FileOffset offset) {
    if (file == nullptr) {
        return -1;
    }
    ssize_t totalBytes = 0;
    const auto* bytePtr = static_cast<const char*>(buf);
    int64_t currentOffset = offset.value;
    while (count > 0) {
        ssize_t bytesWritten = ::pwrite(file->fd, bytePtr, count, currentOffset);
        if (bytesWritten < 0) {
            return -1;
        }
        if (bytesWritten == 0) {
            break;
        }
        totalBytes += bytesWritten;
        bytePtr += bytesWritten;
        currentOffset += bytesWritten;
        count -= static_cast<size_t>(bytesWritten);
    }
    return totalBytes;
}

void close_file(File* file) {
    std::unique_ptr<File> owned(file);
    if (!owned) {
        return;
    }
    if (owned->fd >= 0) {
        ::close(owned->fd);
    }
}

void* big_alloc(size_t bytes) {
    void* allocatedPtr = nullptr;
    if (posix_memalign(&allocatedPtr, 4096, bytes) != 0) {
        return nullptr;
    }
    return allocatedPtr;
}

void big_free(void* ptr, size_t /*bytes*/) { std::free(ptr); }

uint32_t last_error() { return static_cast<uint32_t>(errno); }

}  // namespace mftlib::platform
