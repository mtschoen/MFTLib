#include "pch.h"

#include <iostream>

#include "MFTFileRecord.h"
#include "NativeStructs.h"

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

static MFTFileRecord ParseFileRecord(void* fileRecordPtr)
{
    FileRecordHeader fileRecord = *(FileRecordHeader*)fileRecordPtr;

#if _DEBUG
    // Ensure that the first 4 bytes spell FILE
    auto magicNumber = fileRecord.MagicNumber;
    if (magicNumber[0] != 'F' || magicNumber[1] != 'I' || magicNumber[2] != 'L' || magicNumber[3] != 'E')
    {
        // TODO: Handle error
        wprintf(L"File record fails magic number check (first 4 bytes should spell FILE)\n");
        return MFTFileRecord();
        //throw new ArgumentException("File record fails magic number check (first 4 bytes should spell FILE)");
    }
#endif

    //Console.WriteLine("FileRecordHeader:");
    //Console.WriteLine($"  UpdateSequenceOffset: {fileRecord.UpdateSequenceOffset}");
    //Console.WriteLine($"  UpdateSequenceSize: {fileRecord.UpdateSequenceSize}");
    //Console.WriteLine($"  LogFileSequenceNumber: {fileRecord.LogFileSequenceNumber}");
    //Console.WriteLine($"  SequenceNumber: {fileRecord.SequenceNumber}");
    //Console.WriteLine($"  HardLinkCount: {fileRecord.HardLinkCount}");
    //Console.WriteLine($"  FirstAttributeOffset: {fileRecord.FirstAttributeOffset}");
    //Console.WriteLine($"  Flags: {fileRecord.Flags}");
    //Console.WriteLine($"  UsedSize: {fileRecord.RealSize}");
    //Console.WriteLine($"  AllocatedSize: {fileRecord.AllocatedSize}");
    //Console.WriteLine($"  FileReferenceToBaseRecord: {fileRecord.BaseFileRecord}");
    //Console.WriteLine($"  NextAttributeId: {fileRecord.NextAttributeId}");

    auto attributeOffset = fileRecord.FirstAttributeOffset;
    auto end = fileRecord.AllocatedSize;
    auto mftFileRecord = MFTFileRecord();
    while (attributeOffset < end)
    {
        auto attributePtr = (uint8_t*)fileRecordPtr + attributeOffset;

        // Read the attribute type before we read the header in case the memory after the end marker isn't readable
        auto attributeId = *(AttributeType*)attributePtr;
        if (attributeId == EndMarker)
        {
            break;
        }

        auto attributeHeader = (AttributeHeader*)attributePtr;
        switch (attributeId)
        {
        case StandardInformation:
            //Console.WriteLine($"StandardInformation at offset {attributeOffset}");
            //attributeOffset += 0x4; // Skip the attribute header
            //auto standardInformation = Marshal.PtrToStructure<StandardInformationAttribute>(fileRecordPtr + attributeOffset);
            //attributeOffset += 0x48; // Skip to the next attribute
            break;
        case AttributeList:
            //Console.WriteLine($"AttributeList at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case FileName:
        {
            //Console.WriteLine($"FileName at offset {attributeOffset}");
            auto fileNameAttributePtr = attributePtr + attributeHeader->AttributeOffset;
            auto fileNameAttribute = (FileNameAttribute*)fileNameAttributePtr;
            mftFileRecord.FileName = (LPCWSTR)&fileNameAttribute->FileName;
            //Console.WriteLine($"File name is: {mftFileRecord.FileName}");
            break;
        }
        case ObjectId:
        {
            //Console.WriteLine($"ObjectId at offset {attributeOffset}");
            auto objAttributePtr = attributePtr + attributeHeader->AttributeOffset;
            auto objectAttribute = (ObjectIdAttribute*)objAttributePtr;
            mftFileRecord.Guid = objectAttribute->ObjectId;
            //Console.WriteLine($"Object Id is: {guid}");
            break;
        }
        case SecurityDescriptor:
            //Console.WriteLine($"SecurityDescriptor at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case VolumeName:
            //Console.WriteLine($"VolumeName at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case VolumeInformation:
            //Console.WriteLine($"VolumeInformation at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case Data:
            //Console.WriteLine($"Data at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case IndexRoot:
            //Console.WriteLine($"IndexRoot at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case IndexAllocation:
            //Console.WriteLine($"IndexAllocation at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case Bitmap:
            //Console.WriteLine($"Bitmap at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case ReparsePoint:
            //Console.WriteLine($"ReparsePoint at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case EAInformation:
            //Console.WriteLine($"EAInformation at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case EA:
            //Console.WriteLine($"EA at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case PropertySet:
            //Console.WriteLine($"PropertySet at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case LoggedUtilityStream:
            //Console.WriteLine($"LoggedUtilityStream at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        default:
            //throw new ArgumentOutOfRangeException($"Unknown attribute type {attributeId}");
            break;
        }

        attributeOffset += attributeHeader->Length;
    }

    //Console.WriteLine($"Parsing file record with size {fileRecord.AllocatedSize}");
    //if (string.IsNullOrEmpty(fileName))
    //    throw new InvalidOperationException($"Could not get filename for FileRecord at {fileRecordPtr}");

    return mftFileRecord;
}

extern "C" {
    EXPORT void ExtractMFTRecords(HANDLE volumeHandle, MFTFileRecord* files, ULONG fileCount) {
        HANDLE hFile = CreateFile(L"\\??\\C:\\$MFT", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);

        if (hFile == INVALID_HANDLE_VALUE)
        {
            printf("Failed to open fileee. Error: %u\n", GetLastError());
            return;
        }

        BYTE buffer[4096];
        DWORD bytesRead;

        while (ReadFile(hFile, buffer, sizeof(buffer), &bytesRead, NULL) && bytesRead > 0)
        {
            // Process the buffer here
            printf("Read %u bytes\n", bytesRead);
        }

        CloseHandle(hFile);
    }
}
