#include "pch.h"

#include "framework.h"
#include "ntfs.h"

#define EXPORT __declspec(dllexport)

NTFS_BPB bootSector;

uint8_t mftFile[FILE_RECORD_SIZE];

#define MFT_FILES_PER_BUFFER (65536)
uint8_t mftBuffer[MFT_FILES_PER_BUFFER * FILE_RECORD_SIZE];

static BOOL Read(HANDLE handle, void* buffer, uint64_t from, DWORD count, PDWORD bytesRead) {
    LONG high = from >> 32;
    SetFilePointer(handle, from & 0xFFFFFFFF, &high, FILE_BEGIN);
    return ReadFile(handle, buffer, count, bytesRead, nullptr);
}

extern "C" {
    EXPORT bool ParseMFT(HANDLE volumeHandle) {
        if (volumeHandle == INVALID_HANDLE_VALUE)
        {
            printf("Error in ParseMFT: Volume handle is invalid.");
            return false;
        }

        // Assume cluster size is 512 bytes; ReadFile on volume handles must be integer multiples of sector size
        // https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering
        constexpr DWORD bootSectorSize = 512;
        DWORD bytesRead;
        if (!Read(volumeHandle, &bootSector, 0, bootSectorSize, &bytesRead) || bytesRead != bootSectorSize)
        {
            printf("Error in ParseMFT:Failed to read boot sector. Error: %lu\n", GetLastError());
            return false;
        }

        if (bootSector.name[0] != 'N' || bootSector.name[1] != 'T' || bootSector.name[2] != 'F' || bootSector.name[3] != 'S')
        {
            printf("Error in ParseMFT: Volume is not NTFS.");
            return false;
        }

        auto bytesPerCluster = bootSector.bytesPerSector * bootSector.sectorsPerCluster;

        // TODO: Fall back to MFT mirror if MFT is corrupted
        bytesRead = 0;
        if (!Read(volumeHandle, &mftFile, bootSector.mftStart * bytesPerCluster, FILE_RECORD_SIZE, &bytesRead) || bytesRead != FILE_RECORD_SIZE)
        {
            printf("Error in ParseMFT:Failed to read MFT file. Error: %lu\n", GetLastError());
            return false;
        }

        return true;
    }

    EXPORT void PrintVolumeInfo(HANDLE volumeHandle) {
        if (volumeHandle == INVALID_HANDLE_VALUE)
        {
            printf("Error in PrintVolumeInfo: Volume handle is invalid.");
            return;
        }

        auto combinedVolumeData = NTFS_COMBINED_VOLUME_DATA();
        DWORD bytesReturned;

        try
        {
            DWORD combinedBufferSize = sizeof(NTFS_COMBINED_VOLUME_DATA);
            if (!DeviceIoControl(volumeHandle, FSCTL_GET_NTFS_VOLUME_DATA, nullptr, 0, &combinedVolumeData, combinedBufferSize, &bytesReturned, nullptr))
            {
                printf("Failed to get NTFS volume data. Error: %lu\n", GetLastError());
                return;
            }

            if (bytesReturned != combinedBufferSize)
            {
                printf("Failed to get NTFS volume data. Buffer sizes don't match. Error: %lu\n", GetLastError());
                return;
            }

            auto volumeData = combinedVolumeData.StandardData;
            printf("Volume Serial Number: %lld\n", volumeData.VolumeSerialNumber.QuadPart);
            printf("Number Sectors: %lld\n", volumeData.NumberSectors.QuadPart);
            printf("Total Clusters: %lld\n", volumeData.TotalClusters.QuadPart);
            printf("Free Clusters: %lld\n", volumeData.FreeClusters.QuadPart);
            printf("Total Reserved: %lld\n", volumeData.TotalReserved.QuadPart);
            printf("Bytes Per Sector: %lu\n", volumeData.BytesPerSector);
            printf("Bytes Per Cluster: %lu\n", volumeData.BytesPerCluster);
            printf("Bytes Per File Record Segment: %lu\n", volumeData.BytesPerFileRecordSegment);
            printf("Clusters Per File Record Segment: %lu\n", volumeData.ClustersPerFileRecordSegment);
            printf("MFT Valid Data Length: %lld\n", volumeData.MftValidDataLength.QuadPart);
            printf("MFT Start LCN: %lld\n", volumeData.MftStartLcn.QuadPart);
            printf("MFT2 Start LCN: %lld\n", volumeData.Mft2StartLcn.QuadPart);
            printf("MFT Zone Start: %lld\n", volumeData.MftZoneStart.QuadPart);
            printf("MFT Zone End: %lld\n", volumeData.MftZoneEnd.QuadPart);

            auto extendedVolumeData = combinedVolumeData.ExtendedData;
            printf("Byte Count: %lu\n", extendedVolumeData.ByteCount);
            printf("Major Version: %d\n", extendedVolumeData.MajorVersion);
            printf("Minor Version: %d\n", extendedVolumeData.MinorVersion);
            printf("Bytes Per Physical Sector: %lu\n", extendedVolumeData.BytesPerPhysicalSector);
            printf("Lfs Major Version: %d\n", extendedVolumeData.LfsMajorVersion);
            printf("Lfs Minor Version: %d\n", extendedVolumeData.LfsMinorVersion);
        }
        catch (...)
        {
            printf("Failed to get volume info. Error: %lu\n", GetLastError());
        }
    }
}
