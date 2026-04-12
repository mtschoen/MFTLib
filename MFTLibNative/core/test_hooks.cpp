#include "pch.h"

#include <thread>

#include "../internal.h"

static unsigned g_maxThreads = 0;
static int g_allocFailCountdown = 0;
static int g_readFailCountdown = 0;

unsigned EffectiveThreadCount() {
    unsigned n = std::thread::hardware_concurrency();
    if (n < 1) n = 1;
    if (g_maxThreads > 0 && g_maxThreads < n) n = g_maxThreads;
    return n;
}

bool ShouldFailAlloc() {
    if (g_allocFailCountdown <= 0) return false;
    return --g_allocFailCountdown == 0;
}

bool ShouldFailRead() {
    if (g_readFailCountdown <= 0) return false;
    return --g_readFailCountdown == 0;
}

extern "C" {
    EXPORT void SetMaxThreads(unsigned maxThreads) { g_maxThreads = maxThreads; }
    EXPORT void SetAllocFailCountdown(int countdown) { g_allocFailCountdown = countdown; }
    EXPORT void SetReadFailCountdown(int countdown) { g_readFailCountdown = countdown; }
    EXPORT void ResetTestState() { g_maxThreads = 0; g_allocFailCountdown = 0; g_readFailCountdown = 0; }
}
