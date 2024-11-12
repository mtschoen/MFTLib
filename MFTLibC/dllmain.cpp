#include "pch.h"
#include <windows.h>
#include <cstdio>

#define EXPORT __declspec(dllexport)

BOOL APIENTRY DllMain(HMODULE hModule, DWORD  ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

extern "C" {
    EXPORT void PrintFileSize(LPCWSTR filePath) {
        HANDLE hFile = CreateFileW(filePath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (hFile == INVALID_HANDLE_VALUE) {
            wprintf(L"CreateFile failed with error %lu\n", GetLastError());
            return;
        }

        LARGE_INTEGER fileSize;
        if (!GetFileSizeEx(hFile, &fileSize)) {
            wprintf(L"GetFileSizeEx failed with error %lu\n", GetLastError());
            CloseHandle(hFile);
            return;
        }

        wprintf(L"File size: %lld bytes\n", fileSize.QuadPart);
        CloseHandle(hFile);
    }
}
