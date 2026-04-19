#include "pch.h"

#include "../framework.h"
#include "../mft_api.h"
#include "../internal.h"

// Wraps DeviceIoControl with test hook support.
// When the USN I/O fail countdown fires, returns FALSE with the configured error code.
static BOOL UsnDeviceIoControl(HANDLE handle, DWORD ioControlCode,
    LPVOID inBuffer, DWORD inBufferSize,
    LPVOID outBuffer, DWORD outBufferSize,
    LPDWORD bytesReturned, LPOVERLAPPED overlapped)
{
    DWORD hookError;
    if (ShouldFailUsnIo(hookError)) {
        SetLastError(hookError);
        return FALSE;
    }
    return DeviceIoControl(handle, ioControlCode, inBuffer, inBufferSize,
                           outBuffer, outBufferSize, bytesReturned, overlapped);
}

extern "C" {
    EXPORT UsnJournalInfo* QueryUsnJournal(HANDLE volumeHandle) {
        auto* info = new UsnJournalInfo{};

        USN_JOURNAL_DATA_V0 journalData{};
        DWORD bytesReturned = 0;
        if (!UsnDeviceIoControl(volumeHandle, FSCTL_QUERY_USN_JOURNAL,
                             nullptr, 0,
                             &journalData, sizeof(journalData),
                             &bytesReturned, nullptr)) {
            DWORD error = GetLastError();
            if (error == ERROR_JOURNAL_NOT_ACTIVE)
                (void)swprintf_s(info->errorMessage, 256, L"USN journal is not active");
            else if (error == ERROR_JOURNAL_DELETE_IN_PROGRESS)
                (void)swprintf_s(info->errorMessage, 256, L"USN journal deletion is in progress");
            else
                (void)swprintf_s(info->errorMessage, 256, L"FSCTL_QUERY_USN_JOURNAL failed. Error: %lu", error);
            return info;
        }

        info->journalId       = journalData.UsnJournalID;
        info->firstUsn        = journalData.FirstUsn;
        info->nextUsn         = journalData.NextUsn;
        info->lowestValidUsn  = journalData.LowestValidUsn;
        info->maxUsn          = journalData.MaxUsn;
        info->maximumSize     = journalData.MaximumSize;
        info->allocationDelta = journalData.AllocationDelta;
        return info;
    }

    EXPORT void FreeUsnJournalInfo(UsnJournalInfo* info) {
        delete info;
    }

    EXPORT UsnJournalResult* ReadUsnJournal(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId) {
        auto* result = new UsnJournalResult{};
        result->journalId = journalId;

        constexpr size_t readBufferSize = 64 * 1024;
        auto* readBuffer = ShouldFailAlloc() ? nullptr : (uint8_t*)VirtualAlloc(nullptr, readBufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!readBuffer) {
            (void)swprintf_s(result->errorMessage, 256, L"Failed to allocate read buffer");
            return result;
        }

        constexpr uint64_t initialCapacity = 1024;
        uint64_t capacity = initialCapacity;
        result->entries = ShouldFailAlloc() ? nullptr : (UsnJournalEntry*)VirtualAlloc(
            nullptr, (size_t)capacity * sizeof(UsnJournalEntry), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!result->entries) {
            VirtualFree(readBuffer, 0, MEM_RELEASE);
            (void)swprintf_s(result->errorMessage, 256, L"Failed to allocate entry array");
            return result;
        }

        READ_USN_JOURNAL_DATA_V1 readData;
        readData.StartUsn          = startUsn;
        readData.ReasonMask        = 0xFFFFFFFF;
        readData.ReturnOnlyOnClose = 0;
        readData.Timeout           = 0;
        readData.BytesToWaitFor    = 0;
        readData.UsnJournalID      = journalId;
        readData.MinMajorVersion   = 2;
        readData.MaxMajorVersion   = 2;

        int64_t nextUsn = startUsn;

        for (;;) {
            readData.StartUsn = nextUsn;

            DWORD bytesReturned = 0;
            BOOL ok = UsnDeviceIoControl(volumeHandle, FSCTL_READ_USN_JOURNAL,
                                      &readData, sizeof(readData),
                                      readBuffer, (DWORD)readBufferSize,
                                      &bytesReturned, nullptr);
            if (!ok) {
                DWORD error = GetLastError();
                if (error == ERROR_HANDLE_EOF || error == ERROR_WRITE_PROTECT) {
                    break;
                } else if (error == ERROR_JOURNAL_NOT_ACTIVE) {
                    (void)swprintf_s(result->errorMessage, 256, L"USN journal is not active");
                    break;
                } else if (error == ERROR_JOURNAL_DELETE_IN_PROGRESS) {
                    (void)swprintf_s(result->errorMessage, 256, L"USN journal deletion is in progress");
                    break;
                } else if (error == ERROR_JOURNAL_ENTRY_DELETED) {
                    (void)swprintf_s(result->errorMessage, 256, L"USN journal entries have been deleted; full rescan needed");
                    break;
                } else {
                    (void)swprintf_s(result->errorMessage, 256, L"FSCTL_READ_USN_JOURNAL failed. Error: %lu", error);
                    break;
                }
            }

            if (bytesReturned < sizeof(int64_t)) break;

            int64_t bufferNextUsn;
            memcpy(&bufferNextUsn, readBuffer, sizeof(int64_t));

            if (bufferNextUsn == nextUsn) break;

            nextUsn = bufferNextUsn;

            uint8_t* recordPtr = readBuffer + sizeof(int64_t);
            uint8_t* endPtr    = readBuffer + bytesReturned;

            while (recordPtr + sizeof(USN_RECORD_V2) <= endPtr) {
                auto* usnRecord = (USN_RECORD_V2*)recordPtr;
                if (usnRecord->RecordLength == 0) break;

                if (result->entryCount >= capacity) {
                    uint64_t newCapacity = capacity * 2;
                    auto* grown = (UsnJournalEntry*)VirtualAlloc(
                        nullptr, (size_t)newCapacity * sizeof(UsnJournalEntry),
                        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    if (!grown) {
                        (void)swprintf_s(result->errorMessage, 256, L"Failed to grow entry array");
                        VirtualFree(readBuffer, 0, MEM_RELEASE);
                        result->nextUsn = nextUsn;
                        return result;
                    }
                    memcpy(grown, result->entries, (size_t)result->entryCount * sizeof(UsnJournalEntry));
                    VirtualFree(result->entries, 0, MEM_RELEASE);
                    result->entries = grown;
                    capacity = newCapacity;
                }

                constexpr uint64_t fileRefMask = 0x0000FFFFFFFFFFFF;
                auto& entry = result->entries[result->entryCount];
                memset(&entry, 0, sizeof(UsnJournalEntry));
                entry.recordNumber       = usnRecord->FileReferenceNumber    & fileRefMask;
                entry.parentRecordNumber = usnRecord->ParentFileReferenceNumber & fileRefMask;
                entry.usn                = usnRecord->Usn;
                entry.timestamp          = usnRecord->TimeStamp.QuadPart;
                entry.reason             = usnRecord->Reason;
                entry.fileAttributes     = usnRecord->FileAttributes;

                uint16_t nameLenChars = usnRecord->FileNameLength / sizeof(WCHAR);
                entry.fileNameLength = nameLenChars;
                uint16_t copyLen = min(nameLenChars, (uint16_t)259);
                wmemcpy_s(entry.fileName, 260,
                          (wchar_t*)((uint8_t*)usnRecord + usnRecord->FileNameOffset),
                          copyLen);

                result->entryCount++;
                recordPtr += usnRecord->RecordLength;
            }
        }

        VirtualFree(readBuffer, 0, MEM_RELEASE);
        result->nextUsn = nextUsn;
        return result;
    }

    EXPORT void FreeUsnJournalResult(UsnJournalResult* result) {
        if (result) {
            if (result->entries) VirtualFree(result->entries, 0, MEM_RELEASE);
            delete result;
        }
    }

    EXPORT UsnJournalResult* WatchUsnJournalBatch(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId) {
        auto* result = new UsnJournalResult{};
        result->journalId = journalId;
        result->nextUsn = startUsn;

        constexpr size_t readBufferSize = 64 * 1024;
        auto* readBuffer = ShouldFailAlloc() ? nullptr : (uint8_t*)VirtualAlloc(nullptr, readBufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!readBuffer) {
            (void)swprintf_s(result->errorMessage, 256, L"Failed to allocate read buffer");
            return result;
        }

        READ_USN_JOURNAL_DATA_V1 readData;
        readData.StartUsn          = startUsn;
        readData.ReasonMask        = 0xFFFFFFFF;
        readData.ReturnOnlyOnClose = 0;
        readData.Timeout           = 0;
        readData.BytesToWaitFor    = 1;
        readData.UsnJournalID      = journalId;
        readData.MinMajorVersion   = 2;
        readData.MaxMajorVersion   = 2;

        OVERLAPPED overlapped{};
        overlapped.hEvent = ShouldFailAlloc() ? nullptr : CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!overlapped.hEvent) {
            VirtualFree(readBuffer, 0, MEM_RELEASE);
            (void)swprintf_s(result->errorMessage, 256, L"Failed to create event. Error: %lu", GetLastError());
            return result;
        }

        DWORD bytesReturned = 0;
        BOOL ok = UsnDeviceIoControl(volumeHandle, FSCTL_READ_USN_JOURNAL,
                                  &readData, sizeof(readData),
                                  readBuffer, (DWORD)readBufferSize,
                                  &bytesReturned, &overlapped);

        if (!ok) {
            DWORD error = GetLastError();
            if (error == ERROR_IO_PENDING) {
                ok = GetOverlappedResult(volumeHandle, &overlapped, &bytesReturned, TRUE);
                if (!ok) {
                    error = GetLastError();
                    if (error == ERROR_OPERATION_ABORTED) {
                        CloseHandle(overlapped.hEvent);
                        VirtualFree(readBuffer, 0, MEM_RELEASE);
                        return result;
                    }
                }
            }

            if (!ok) {
                CloseHandle(overlapped.hEvent);
                VirtualFree(readBuffer, 0, MEM_RELEASE);
                DWORD finalError = GetLastError();
                if (finalError == ERROR_HANDLE_EOF || finalError == ERROR_WRITE_PROTECT) {
                    return result;
                } else if (finalError == ERROR_JOURNAL_NOT_ACTIVE) {
                    (void)swprintf_s(result->errorMessage, 256, L"USN journal is not active");
                } else if (finalError == ERROR_JOURNAL_DELETE_IN_PROGRESS) {
                    (void)swprintf_s(result->errorMessage, 256, L"USN journal deletion is in progress");
                } else if (finalError == ERROR_JOURNAL_ENTRY_DELETED) {
                    (void)swprintf_s(result->errorMessage, 256, L"USN journal entries have been deleted; full rescan needed");
                } else {
                    (void)swprintf_s(result->errorMessage, 256, L"FSCTL_READ_USN_JOURNAL watch failed. Error: %lu", finalError);
                }
                return result;
            }
        }

        CloseHandle(overlapped.hEvent);

        if (bytesReturned >= sizeof(int64_t)) {
            int64_t bufferNextUsn;
            memcpy(&bufferNextUsn, readBuffer, sizeof(int64_t));
            result->nextUsn = bufferNextUsn;

            uint64_t count = 0;
            uint8_t* scanPtr = readBuffer + sizeof(int64_t);
            uint8_t* endPtr = readBuffer + bytesReturned;
            while (scanPtr + sizeof(USN_RECORD_V2) <= endPtr) {
                auto* rec = (USN_RECORD_V2*)scanPtr;
                if (rec->RecordLength == 0) break;
                count++;
                scanPtr += rec->RecordLength;
            }

            if (count > 0) {
                result->entries = (UsnJournalEntry*)VirtualAlloc(
                    nullptr, (size_t)count * sizeof(UsnJournalEntry), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (result->entries) {
                    constexpr uint64_t fileRefMask = 0x0000FFFFFFFFFFFF;
                    uint8_t* recordPtr = readBuffer + sizeof(int64_t);
                    for (uint64_t i = 0; i < count && recordPtr + sizeof(USN_RECORD_V2) <= endPtr; i++) {
                        auto* usnRecord = (USN_RECORD_V2*)recordPtr;
                        if (usnRecord->RecordLength == 0) break;

                        auto& entry = result->entries[i];
                        memset(&entry, 0, sizeof(UsnJournalEntry));
                        entry.recordNumber       = usnRecord->FileReferenceNumber & fileRefMask;
                        entry.parentRecordNumber = usnRecord->ParentFileReferenceNumber & fileRefMask;
                        entry.usn                = usnRecord->Usn;
                        entry.timestamp          = usnRecord->TimeStamp.QuadPart;
                        entry.reason             = usnRecord->Reason;
                        entry.fileAttributes     = usnRecord->FileAttributes;

                        uint16_t nameLenChars = usnRecord->FileNameLength / sizeof(WCHAR);
                        uint16_t copyLen = min(nameLenChars, (uint16_t)259);
                        entry.fileNameLength = copyLen;
                        wmemcpy_s(entry.fileName, 260,
                                  (wchar_t*)((uint8_t*)usnRecord + usnRecord->FileNameOffset),
                                  copyLen);

                        result->entryCount++;
                        recordPtr += usnRecord->RecordLength;
                    }
                }
            }
        }

        VirtualFree(readBuffer, 0, MEM_RELEASE);
        return result;
    }

    EXPORT BOOL CancelUsnJournalWatch(HANDLE volumeHandle) {
        return CancelIoEx(volumeHandle, nullptr);
    }
}
