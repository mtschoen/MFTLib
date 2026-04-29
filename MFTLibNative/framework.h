#pragma once

#ifdef _WIN32
    #define WIN32_LEAN_AND_MEAN
    #include <windows.h>
    #include <ioapiset.h>
    #include <fileapi.h>
    #include <handleapi.h>
    #include <winioctl.h>
    #include <minwindef.h>
#endif

#include <cstdio>
#include <cstdint>
#include <memory>
#include <vector>
