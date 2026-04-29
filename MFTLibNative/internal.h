#pragma once

#include <chrono>
#include <cassert>
#include <cstdarg>
#include <cstdio>
#include <cwchar>
#include <cstdint>

#include "core/platform.h"

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

#ifdef _WIN32
bool ShouldFailUsnIo(DWORD& outError);
#endif
