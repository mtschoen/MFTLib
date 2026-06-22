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
    if (!path_utf8) return nullptr;
    int fd = ::open(path_utf8, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return nullptr;
    posix_fadvise(fd, 0, 0, POSIX_FADV_SEQUENTIAL);
    return new File{fd};
}

File* open_write(const char* path_utf8) {
    if (!path_utf8) return nullptr;
    int fd = ::open(path_utf8, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
    if (fd < 0) return nullptr;
    return new File{fd};
}

int64_t size_of(const File* file) {
    if (!file) return -1;
    struct stat st{};
    if (::fstat(file->fd, &st) != 0) return -1;
    return static_cast<int64_t>(st.st_size);
}

int64_t pread_at(const File* file, void* buf, size_t count, int64_t offset) {
    if (!file) return -1;
    ssize_t total = 0;
    auto* p = static_cast<char*>(buf);
    while (count > 0) {
        ssize_t n = ::pread(file->fd, p, count, offset);
        if (n < 0) return -1;
        if (n == 0) break;  // EOF
        total += n;
        p += n;
        offset += n;
        count -= static_cast<size_t>(n);
    }
    return total;
}

int64_t pwrite_at(const File* file, const void* buf, size_t count, int64_t offset) {
    if (!file) return -1;
    ssize_t total = 0;
    const auto* p = static_cast<const char*>(buf);
    while (count > 0) {
        ssize_t n = ::pwrite(file->fd, p, count, offset);
        if (n < 0) return -1;
        if (n == 0) break;
        total += n;
        p += n;
        offset += n;
        count -= static_cast<size_t>(n);
    }
    return total;
}

void close_file(File* file) {
    std::unique_ptr<File> owned(file);
    if (!owned) return;
    if (owned->fd >= 0) ::close(owned->fd);
}

void* big_alloc(size_t bytes) {
    void* p = nullptr;
    if (posix_memalign(&p, 4096, bytes) != 0) return nullptr;
    return p;
}

void big_free(void* ptr, size_t /*bytes*/) { std::free(ptr); }

uint32_t last_error() { return static_cast<uint32_t>(errno); }

}  // namespace mftlib::platform
