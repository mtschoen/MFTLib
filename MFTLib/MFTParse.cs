using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MFTLib;

public class MFTParse
{
    static readonly int k_OffsetToFileNameUnicode = Marshal.OffsetOf<FileNameAttributeHeader>(nameof(FileNameAttributeHeader.FileName)).ToInt32();

    // ReSharper disable InconsistentNaming
    const uint GENERIC_READ = 0x80000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    // ReSharper restore InconsistentNaming

    public static unsafe MFTNode GetMFTNode(string volume)
    {
        if (string.IsNullOrEmpty(volume))
        {
            throw new ArgumentException("Volume name cannot be null or empty", nameof(volume));
        }

#if DEBUG
        var stopwatch = new Stopwatch();
        stopwatch.Start();
#endif

        var volumeHandle = Kernel32.CreateFile(
            volume,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            throw new IOException($"Unable to open volume {volume}", Marshal.GetLastWin32Error());
        }

        const int bootSectorLength = 512;
        var readBuffer = new byte[bootSectorLength];
        Kernel32.ReadFile(volumeHandle, readBuffer, bootSectorLength, out _, IntPtr.Zero);
        BootSector* bootSector;
        fixed (byte* readBufferPtr = readBuffer)
        {
            bootSector = (BootSector*)readBufferPtr;
        }

        var bytesPerCluster = (ulong)bootSector->bytesPerSector * bootSector->sectorsPerCluster;
        var distanceToMove = (long)(bootSector->mftStart * bytesPerCluster);

        // TODO: Check volume data for mft File Size
        const int mftFileSize = 1024;
        readBuffer = new byte[mftFileSize];
        Kernel32.SetFilePointerEx(volumeHandle, distanceToMove, IntPtr.Zero, 0);
        Kernel32.ReadFile(volumeHandle, readBuffer, mftFileSize, out _, IntPtr.Zero);

        List<MFTLibFile> files = new();
        var notInUseCount = 0;

        fixed (byte* readBufferPtr = readBuffer)
        {
            var fileRecord = (FileRecordHeader*)readBufferPtr;
            var attribute = (AttributeHeader*)(readBufferPtr + fileRecord->firstAttributeOffset);
            NonResidentAttributeHeader* dataAttribute = null;
            ulong approximateRecordCount = 0;
            Debug.Assert(fileRecord->magic == FileRecordHeader.kMagicNumber);

            while (true)
            {
                if (attribute->attributeType == AttributeType.Data)
                {
                    dataAttribute = (NonResidentAttributeHeader*)attribute;
                }
                else if (attribute->attributeType == AttributeType.Bitmap)
                {
                    approximateRecordCount = ((NonResidentAttributeHeader*)attribute)->attributeSize * 8;
                }
                else if (attribute->attributeType == AttributeType.EndMarker)
                {
                    break;
                }

                attribute = (AttributeHeader*)((byte*)attribute + attribute->length);
            }

            Debug.Assert(dataAttribute != null);

            var dataRun = (RunHeader*)((byte*)dataAttribute + dataAttribute->dataRunsOffset);
            ulong clusterNumber = 0;
            ulong recordsProcessed = 0;

            const ulong mftFilesPerBuffer = 65536;
            var mftBuffer = new byte[mftFileSize * mftFilesPerBuffer];

            while (((byte*)dataRun - (byte*)dataAttribute) < dataAttribute->standard.length &&
                   dataRun->lengthFieldBytes != 0)
            {
                ulong length = 0, offset = 0;

                for (int i = 0; i < dataRun->lengthFieldBytes; i++)
                {
                    length |= (ulong)(((byte*)dataRun)[1 + i]) << (i * 8);
                }

                for (int i = 0; i < dataRun->offsetFieldBytes; i++)
                {
                    offset |= (ulong)(((byte*)dataRun)[1 + dataRun->lengthFieldBytes + i]) << (i * 8);
                }

                if ((offset & ((ulong)1 << (dataRun->offsetFieldBytes * 8 - 1))) != 0)
                {
                    for (int i = dataRun->offsetFieldBytes; i < 8; i++)
                    {
                        offset |= (ulong)0xFF << (i * 8);
                    }
                }

                clusterNumber += offset;
                dataRun = (RunHeader*)((byte*)dataRun + 1 + dataRun->lengthFieldBytes + dataRun->offsetFieldBytes);

                ulong filesRemaining = length * bytesPerCluster / mftFileSize;
                ulong positionInBlock = 0;

                while (filesRemaining > 0)
                {
                    Console.WriteLine("{0}", (int)(recordsProcessed * 100 / approximateRecordCount));

                    ulong filesToLoad = mftFilesPerBuffer;
                    if (filesRemaining < mftFilesPerBuffer)
                        filesToLoad = filesRemaining;

                    distanceToMove = (long)(clusterNumber * bytesPerCluster + positionInBlock);
                    Kernel32.SetFilePointerEx(volumeHandle, distanceToMove, IntPtr.Zero, 0);
                    Kernel32.ReadFile(volumeHandle, mftBuffer, (uint)(filesToLoad * mftFileSize), out _, IntPtr.Zero);

                    positionInBlock += filesToLoad * mftFileSize;
                    filesRemaining -= filesToLoad;

                    fixed (byte* mftBufferPtr = mftBuffer)
                    {
                        for (ulong i = 0; i < filesToLoad; i++)
                        {
                            // Even on an SSD, processing the file records takes only a fraction of the time to read the data,
                            // so there's not much point in multithreading this.

                            fileRecord = (FileRecordHeader*)(mftBufferPtr + mftFileSize * i);
                            recordsProcessed++;

                            if (!fileRecord->inUse)
                            {
                                notInUseCount++;
                                continue;
                            }

                            attribute = (AttributeHeader*)((byte*)fileRecord + fileRecord->firstAttributeOffset);
                            Debug.Assert(fileRecord->magic == FileRecordHeader.kMagicNumber);

                            while ((byte*)attribute - (byte*)fileRecord < mftFileSize)
                            {
                                if (attribute->attributeType == AttributeType.FileName)
                                {
                                    var fileNameAttribute = (FileNameAttributeHeader*)attribute;
                                    if (fileNameAttribute->namespaceType != 2 &&
                                        fileNameAttribute->resident.standard.nonResident == 0)
                                    {
                                        var fileNamePtr = (byte*)fileNameAttribute + k_OffsetToFileNameUnicode;
                                        files.Add(new MFTLibFile
                                        {
                                            Parent = fileNameAttribute->parentRecordNumber,
                                            FileName = Marshal.PtrToStringUni(new IntPtr(fileNamePtr), fileNameAttribute->fileNameLength)
                                        });
                                    }
                                }
                                else if (attribute->attributeType == AttributeType.EndMarker)
                                {
                                    break;
                                }

                                attribute = (AttributeHeader*)((byte*)attribute + attribute->length);
                            }
                        }
                    }
                }
            }
            
            Console.WriteLine($"Found {files.Count} files.");
            Console.WriteLine($"Found {notInUseCount} files not in use.");

            Kernel32.CloseHandle(volumeHandle);

            return null;
        }
    }
}
