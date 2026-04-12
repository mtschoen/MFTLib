#pragma once

#include <chrono>

#define EXPORT __declspec(dllexport)

using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

static inline double ElapsedMs(TimePoint start, TimePoint end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

// Test hook declarations (defined in core/test_hooks.cpp)
unsigned EffectiveThreadCount();
bool ShouldFailAlloc();
bool ShouldFailRead();
