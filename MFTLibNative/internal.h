#pragma once

#include <chrono>
#include <cassert>
#include <cstdio>
#include <cstdint>

#ifdef _WIN32
    #define EXPORT __declspec(dllexport)
#else
    #define EXPORT __attribute__((visibility("default")))
#endif

using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

static inline double ElapsedMs(TimePoint start, TimePoint end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

// Writes a formatted wide error message to a fixed-size buffer. Silently
// truncates and always null-terminates; asserts in debug builds when the
// message didn't fit. Buffer size is deduced from the array reference.
template <size_t N, typename... Args>
// NOLINTNEXTLINE(modernize-avoid-c-arrays): array-reference parameter deduces the fixed C-ABI buffer size
inline void SetErrorMessage(wchar_t (&buffer)[N], const wchar_t* format, Args... arguments) {
#ifdef _WIN32
    int written = _snwprintf_s(buffer, N, _TRUNCATE, format, arguments...);
#else
    int written = std::swprintf(buffer, N, format, arguments...);
    if (written < 0 || static_cast<size_t>(written) >= N) buffer[N - 1] = L'\0';
#endif
    assert(written >= 0 && "error message truncated");
}

// Test hook declarations (defined in core/test_hooks.cpp)
unsigned EffectiveThreadCount();
bool ShouldFailAlloc();
bool ShouldFailRead();
uint64_t NamePoolCapacityOverride();
bool ShouldFailFileSize();
bool ShouldFailPathConversion();
// Force the platform positioned read/write to take their failure branch, so
// pread_at/pwrite_at error handling is coverable without a real I/O failure.
bool ShouldFailPlatformRead();
bool ShouldFailPlatformWrite();

#ifdef _WIN32
bool ShouldFailUsnIo(DWORD& outError);
// Test seam: inject a synthetic IOCTL success response (dequeues one buffer
// queued via SetUsnIoSuccess), so USN journal success + record-parsing paths
// can be covered without a real elevated volume handle.
bool UsnIoInjectSuccess(void* outBuffer, unsigned long outBufferSize, unsigned long* bytesReturned);
// Test seam: force the overlapped wait to report ERROR_OPERATION_ABORTED,
// exercising the watch cancel path without a real pending IOCTL.
bool UsnIoShouldAbortOverlapped();
#endif
