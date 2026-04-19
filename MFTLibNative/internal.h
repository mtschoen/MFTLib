#pragma once

#include <chrono>
#include <cassert>
#include <cstdarg>
#include <cwchar>

#define EXPORT __declspec(dllexport)

using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

static inline double ElapsedMs(TimePoint start, TimePoint end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

// Writes a formatted wide error message to a fixed-size buffer. Silently
// truncates and always null-terminates; asserts in debug builds when the
// message didn't fit. Buffer size is deduced from the array reference.
template <size_t N>
inline void SetErrorMessage(wchar_t (&buffer)[N], const wchar_t* format, ...) {
    va_list arguments;
    va_start(arguments, format);
    int written = _vsnwprintf_s(buffer, N, _TRUNCATE, format, arguments);
    va_end(arguments);
    assert(written >= 0 && "error message truncated");
}

// Test hook declarations (defined in core/test_hooks.cpp)
unsigned EffectiveThreadCount();
bool ShouldFailAlloc();
bool ShouldFailRead();
bool ShouldFailUsnIo(DWORD& outError);
