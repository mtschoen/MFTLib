using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

/// <summary>
///     Tests for MftVolume, MftResult, and FileUtilities using mocked native calls.
///     These run without admin elevation.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MockVolumeTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        Kernel32.ResetToDefaults();
    }

    static SafeFileHandle FakeHandle()
    {
        return new SafeFileHandle(new IntPtr(1), false);
    }

    static unsafe IntPtr BuildResult(uint usedRecords, string? errorMessage = null)
    {
        var entryBufSize = (int)(MFTLibNative.NativeCompactEntrySize * usedRecords);
        var entryBuf = Marshal.AllocHGlobal(entryBufSize);
        new Span<byte>((void*)entryBuf, entryBufSize).Clear();

        var strings = new List<string>();
        var totalStringUnits = 0;
        for (uint i = 0; i < usedRecords; i++)
        {
            var str = $"file{i}.txt";
            strings.Add(str);
            totalStringUnits += str.Length;
        }

        var stringBuf = totalStringUnits > 0 ? Marshal.AllocHGlobal(totalStringUnits * sizeof(char)) : IntPtr.Zero;
        var currentOffset = 0UL;
        var stringSpan = stringBuf != IntPtr.Zero
            ? new Span<char>((void*)stringBuf, totalStringUnits)
            : Span<char>.Empty;

        for (uint i = 0; i < usedRecords; i++)
        {
            var str = strings[(int)i];
            var ptr = (byte*)entryBuf + i * MFTLibNative.NativeCompactEntrySize;
            Unsafe.WriteUnaligned(ptr, (ulong)i); // recordNumber
            Unsafe.WriteUnaligned(ptr + 8, 5UL); // parentRecordNumber
            Unsafe.WriteUnaligned(ptr + 16, currentOffset); // stringOffset
            Unsafe.WriteUnaligned(ptr + 24, (uint)FileAttributes.Normal); // fileAttributes
            Unsafe.WriteUnaligned(ptr + 28, (ushort)1); // flags = InUse
            Unsafe.WriteUnaligned(ptr + 30, (ushort)str.Length); // stringLength

            str.AsSpan().CopyTo(stringSpan.Slice((int)currentOffset, str.Length));
            currentOffset += (ulong)str.Length;
        }

        var result = new MftParseResult
        {
            TotalRecords = usedRecords,
            UsedRecords = usedRecords,
            Entries = entryBuf,
            EntryStrings = stringBuf,
            EntryStringUnits = (ulong)totalStringUnits,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize,
            ErrorMessage = errorMessage ?? string.Empty
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    static void SetupMocks(uint usedRecords = 3)
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        var resultPtr = BuildResult(usedRecords);
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = FreeBuiltResult;
    }

    static void FreeBuiltResult(IntPtr pointer)
    {
        var parseResult = Marshal.PtrToStructure<MftParseResult>(pointer);
        if (parseResult.Entries != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(parseResult.Entries);
        }

        if (parseResult.EntryStrings != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(parseResult.EntryStrings);
        }

        Marshal.FreeHGlobal(pointer);
    }

    // --- FileUtilities ---

    [TestMethod]
    public void GetVolumeHandle_InvalidHandle_ThrowsIOException()
    {
        Kernel32._createFile = (_, _, _, _, _, _, _) => new SafeFileHandle(new IntPtr(-1), false);

        Assert.ThrowsException<IOException>(() =>
            FileUtilities._getVolumeHandle(@"\\.\C:"));
    }

    [TestMethod]
    public void GetVolumeHandle_ValidHandle_ReturnsHandle()
    {
        Kernel32._createFile = (_, _, _, _, _, _, _) => FakeHandle();

        using var handle = FileUtilities._getVolumeHandle(@"\\.\C:");
        Assert.IsFalse(handle.IsInvalid);
    }

    // --- MftVolume.Open ---

    [TestMethod]
    public void Open_ValidVolume_ReturnsOpenVolume()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        using var volume = MftVolume.Open("C");
        Assert.IsNotNull(volume);
    }

    [TestMethod]
    public void Dispose_DisposesHandle()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        SafeFileHandle handle;
        using (var volume = MftVolume.Open("C"))
        {
            handle = volume.GetVolumeHandleForTest();
        }

        Assert.IsTrue(handle.IsClosed);
    }

    [TestMethod]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var volume = MftVolume.Open("C");
        volume.Dispose();
        volume.Dispose(); // Should not throw
    }

    [TestMethod]
    public void Methods_AfterDispose_ThrowObjectDisposed()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var volume = MftVolume.Open("C");
        volume.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => volume.ReadAll());
        Assert.ThrowsException<ObjectDisposedException>(() => volume.StreamRecords(false, null, null, CancellationToken.None));
    }

    // --- ReadAllRecords ---

    [TestMethod]
    public void ReadAllRecords_ReturnsRecords()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAll();

        Assert.AreEqual(3, records.Length);
        Assert.AreEqual(0UL, records[0].RecordNumber);
        Assert.AreEqual("file0.txt", records[0].FileName);
    }

    [TestMethod]
    public void ReadAllRecords_WithTimings_PopulatesTimings()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAll(out _, out var totalRecords);

        Assert.AreEqual(3, records.Length);
        Assert.AreEqual(3UL, totalRecords);
    }

    // --- StreamRecords ---

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StreamRecords_PassesIncludeFreedToTheNativeParser(bool includeFreed)
    {
        bool? requested = null;
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        MFTLibNative._parseMftRecordsWithProgress = (_, freed, _, _, _) =>
        {
            requested = freed;
            return BuildResult(1);
        };
        MFTLibNative._freeMftResult = FreeBuiltResult;
        using var volume = MftVolume.Open("C");

        using var stream = volume.StreamRecords(includeFreed, null, null, CancellationToken.None);

        Assert.AreEqual(includeFreed, requested);
        Assert.AreEqual(1UL, stream.TotalRecords);
    }


    [TestMethod]
    public void StreamRecords_ReturnsEnumerableStream()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        using var stream = volume.StreamRecords(false, null, null, CancellationToken.None);

        Assert.AreEqual(3UL, stream.TotalRecords);
        Assert.AreEqual(3UL, stream.UsedRecords);

        var list = stream.ToList();
        Assert.AreEqual(3, list.Count);
        Assert.AreEqual("file0.txt", list[0].FileName);
    }

    [TestMethod]
    public void StreamRecords_NonGenericEnumerator_Works()
    {
        SetupMocks(2);

        using var volume = MftVolume.Open("C");
        using var stream = volume.StreamRecords(false, null, null, CancellationToken.None);

        var count = 0;
        foreach (var item in (IEnumerable)stream)
        {
            Assert.IsInstanceOfType<MftRecord>(item);
            count++;
        }

        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public void MftResult_NativeCompactBytes_ComputesCorrectSize()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        using var stream = volume.StreamRecords(false, null, null, CancellationToken.None);

        // 3 records * 52 bytes + string units (file0.txt=9, file1.txt=9, file2.txt=9 = 27 units * 2 bytes = 54)
        // 156 + 54 = 210 bytes
        Assert.AreEqual(210UL, stream.NativeCompactBytes);
    }

    [TestMethod]
    public void MftResult_Dispose_TotalsAndCompactBytesRemainReadable()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        var stream = volume.StreamRecords(false, null, null, CancellationToken.None);
        stream.Dispose();

        Assert.AreEqual(3UL, stream.TotalRecords);
        Assert.AreEqual(3UL, stream.UsedRecords);
        Assert.AreEqual(210UL, stream.NativeCompactBytes);
        Assert.IsNotNull(stream.Timings);
    }

    // --- ParseMFTFromFile ---

    // --- MftResult Error and Dispose ---

    [TestMethod]
    public void MftResult_ErrorMessage_ThrowsInvalidOperation()
    {
        var errorResultPtr = BuildResult(0, errorMessage: "Volume read failed");
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new MftResult(errorResultPtr));

        Assert.AreEqual("Volume read failed", ex.Message);
    }

    [TestMethod]
    public void MftVolume_GetVolumeHandleForTest_ReturnsHandle()
    {
        SafeFileHandle? handle = null;
        try
        {
            FileUtilities._getVolumeHandle = _ => handle = FakeHandle();
            using var volume = MftVolume.Open("C");
            Assert.AreSame(handle, volume.GetVolumeHandleForTest());
        }
        finally
        {
            handle?.Dispose();
        }
    }

    [TestMethod]
    public void MftResult_Dispose_FreesNativeResult()
    {
        var freed = false;
        var resultPtr = BuildResult(1);
        MFTLibNative._freeMftResult = ptr =>
        {
            var p = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (p.Entries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(p.Entries);
            }

            if (p.EntryStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(p.EntryStrings);
            }

            Marshal.FreeHGlobal(ptr);
            freed = true;
        };

        var result = new MftResult(resultPtr);
        result.Dispose();

        Assert.IsTrue(freed);
    }

    [TestMethod]
    public void MftResult_Dispose_CalledTwice_FreesOnlyOnce()
    {
        var freeCount = 0;
        var resultPtr = BuildResult(1);
        MFTLibNative._freeMftResult = ptr =>
        {
            var p = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (p.Entries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(p.Entries);
            }

            if (p.EntryStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(p.EntryStrings);
            }

            Marshal.FreeHGlobal(ptr);
            freeCount++;
        };

        var result = new MftResult(resultPtr);
        result.Dispose();
        result.Dispose();

        Assert.AreEqual(1, freeCount);
    }

    [TestMethod]
    public void MftResult_EnumerationAfterDispose_ThrowsObjectDisposed()
    {
        var resultPtr = BuildResult(1);
        MFTLibNative._freeMftResult = ptr =>
        {
            var p = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (p.Entries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(p.Entries);
            }

            if (p.EntryStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(p.EntryStrings);
            }

            Marshal.FreeHGlobal(ptr);
        };

        var result = new MftResult(resultPtr);
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(result.GetEnumerator);
        Assert.ThrowsException<ObjectDisposedException>(result.ToArray);
    }

    [TestMethod]
    public void MftResult_ToArray_MaterializesAllRecords()
    {
        SetupMocks(5);

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAll();

        Assert.AreEqual(5, records.Length);
        Assert.AreEqual("file0.txt", records[0].FileName);
        Assert.AreEqual("file4.txt", records[4].FileName);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-50)]
    [DataRow(-100)]
    public void MftResult_MaterializeBatches_ZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int batchSize)
    {
        SetupMocks(5);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
        {
            using var volume = MftVolume.Open("C");
            using var result = volume.StreamRecords(false, null, null, CancellationToken.None);
            _ = result.MaterializeBatches(batchSize).ToList();
        });
    }

    [TestMethod]
    public void MftResult_MaterializeBatches_Disposed_ThrowsObjectDisposedException()
    {
        SetupMocks(5);
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords(false, null, null, CancellationToken.None);
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() =>
            result.MaterializeBatches().ToList());
    }

    [TestMethod]
    public void MftResult_MaterializeBatches_BatchesMatchRecordsInOrder()
    {
        SetupMocks(7);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(false, null, null, CancellationToken.None);

        var batches = result.MaterializeBatches(3).ToList();

        Assert.AreEqual(3, batches.Count);
        Assert.AreEqual(3, batches[0].Length);
        Assert.AreEqual(3, batches[1].Length);
        Assert.AreEqual(1, batches[2].Length);

        var concatenated = batches.SelectMany(b => b).ToArray();
        Assert.AreEqual(7, concatenated.Length);
        for (var i = 0; i < 7; i++)
        {
            Assert.AreEqual((ulong)i, concatenated[i].RecordNumber);
            Assert.AreEqual($"file{i}.txt", concatenated[i].FileName);
        }
    }

    [TestMethod]
    public void MftResult_MaterializeBatches_RecordsStayValidAfterResultDisposed()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords(false, null, null, CancellationToken.None);
        var batches = result.MaterializeBatches(2).ToList();
        result.Dispose();

        Assert.AreEqual(2, batches.Count);
        Assert.AreEqual("file0.txt", batches[0][0].FileName);
        Assert.AreEqual("file1.txt", batches[0][1].FileName);
        Assert.AreEqual("file2.txt", batches[1][0].FileName);
    }

    [TestMethod]
    public void MftVolume_ReadRecordBatches_Disposed_ThrowsObjectDisposedException()
    {
        SetupMocks(5);
        var volume = MftVolume.Open("C");
        volume.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() =>
            volume.ReadRecordBatches(4096, null, null, CancellationToken.None).ToList());
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void MftVolume_ReadRecordBatches_ZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int batchSize)
    {
        SetupMocks(5);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
        {
            using var volume = MftVolume.Open("C");
            _ = volume.ReadRecordBatches(batchSize, null, null, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void MftVolume_ReadRecordBatches_BatchesMatchReadAllRecords()
    {
        SetupMocks(7);
        using var volume = MftVolume.Open("C");
        var batches = volume.ReadRecordBatches(3, null, null, CancellationToken.None).ToList();

        Assert.AreEqual(3, batches.Count);
        Assert.AreEqual(3, batches[0].Length);
        Assert.AreEqual(3, batches[1].Length);
        Assert.AreEqual(1, batches[2].Length);

        var concatenated = batches.SelectMany(b => b).ToArray();
        for (var i = 0; i < 7; i++)
        {
            Assert.AreEqual((ulong)i, concatenated[i].RecordNumber);
            Assert.AreEqual($"file{i}.txt", concatenated[i].FileName);
        }
    }

    [TestMethod]
    public void MftVolume_ReadRecordBatches_EarlyEnumerationDisposal_FreesNativeResult()
    {
        var freed = false;
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        var resultPtr = BuildResult(10);
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = ptr =>
        {
            var parseResult = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (parseResult.Entries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parseResult.Entries);
            }

            if (parseResult.EntryStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parseResult.EntryStrings);
            }

            Marshal.FreeHGlobal(ptr);
            freed = true;
        };

        using var volume = MftVolume.Open("C");
        MftRecord[]? firstBatch = null;
        foreach (var batch in volume.ReadRecordBatches(3, null, null, CancellationToken.None))
        {
            firstBatch = batch;
            break;
        }

        Assert.IsTrue(freed, "Native MftResult should be freed upon early enumeration disposal");
        Assert.IsNotNull(firstBatch);
        Assert.AreEqual(3, firstBatch.Length);
        Assert.AreEqual("file0.txt", firstBatch[0].FileName);
    }

    [TestMethod]
    public unsafe void MftVolume_StreamRecords_RootDirectory_ZeroLengthName_ReadsAsDot()
    {
        var entryBuf = (IntPtr)NativeMemory.AllocZeroed(MFTLibNative.NativeCompactEntrySize);
        var stringBuf = (IntPtr)NativeMemory.AllocZeroed(sizeof(char));

        try
        {
            var ptr = (byte*)entryBuf;
            Unsafe.WriteUnaligned(ptr, 5UL); // recordNumber = 5
            Unsafe.WriteUnaligned(ptr + 8, 5UL); // parent = 5
            Unsafe.WriteUnaligned(ptr + 16, 0UL); // stringOffset
            Unsafe.WriteUnaligned(ptr + 24, (uint)FileAttributes.Directory);
            Unsafe.WriteUnaligned(ptr + 28, (ushort)3); // flags = InUse | Directory
            Unsafe.WriteUnaligned(ptr + 30, (ushort)0); // zero-length name

            var result = new MftParseResult
            {
                TotalRecords = 1,
                UsedRecords = 1,
                Entries = entryBuf,
                EntryStrings = stringBuf,
                EntryStringUnits = 0,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };

            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);

            FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
            MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _) => resultPtr;
            MFTLibNative._freeMftResult = _ => { };

            using var volume = MftVolume.Open("C");
            using var stream = volume.StreamRecords(false, null, null, CancellationToken.None);
            var record = stream.First();

            Assert.AreEqual(".", record.FileName);
            Assert.IsTrue(record.IsDirectory);
            Assert.IsTrue(record.InUse);
            Assert.AreEqual(".", record.Materialize().FileName);
        }
        finally
        {
            NativeMemory.Free((void*)entryBuf);
            NativeMemory.Free((void*)stringBuf);
        }
    }

    [TestMethod]
    public void MftRecord_FileName_NoName_ReturnsEmpty()
    {
        var record = new MftRecord(0, 5, new MftRecordFields(1), fileName: null);
        Assert.AreEqual(string.Empty, record.FileName);
    }

    [TestMethod]
    public void MftRecord_FileAttributes_ReturnsStoredValue()
    {
        var fields = new MftRecordFields(1, FileAttributes.Hidden | FileAttributes.ReadOnly);
        var record = new MftRecord(0, 5, fields, "test.txt");
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.ReadOnly, record.FileAttributes);
    }
}
