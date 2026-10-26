using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
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
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
        Kernel32.Hooks.ResetToDefaults();
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
            : [];

        for (uint i = 0; i < usedRecords; i++)
        {
            var str = strings[(int)i];
            var ptr = (byte*)entryBuf + (i * MFTLibNative.NativeCompactEntrySize);
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
            EntryStride = MFTLibNative.NativeCompactEntrySize,
            ErrorMessage = errorMessage ?? string.Empty
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    static void SetupMocks(uint usedRecords = 3)
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var resultPtr = BuildResult(usedRecords);
        MFTLibNative.Hooks._parseMftRecordsWithProgress = (_, _, _, _, _) => resultPtr;
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
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

    static void Consume<T>(IEnumerable<T> values) => _ = values.ToArray();

    static void PrepareBatches(MftResult result, bool consumeFirst, out IEnumerable<MftRecord[]> batches)
    {
        batches = result.ReadBatches(1);
        if (consumeFirst)
        {
            Consume(batches);
        }
    }

    // --- FileUtilities ---

    [TestMethod]
    public void GetVolumeHandle_InvalidHandle_ThrowsIOException()
    {
        Kernel32.Hooks._createFile = (_, _, _, _, _, _, _) => new SafeFileHandle(new IntPtr(-1), false);

        Assert.ThrowsExactly<IOException>(() =>
            FileUtilities.Hooks._getVolumeHandle(@"\\.\C:"));
    }

    [TestMethod]
    public void GetVolumeHandle_ValidHandle_ReturnsHandle()
    {
        Kernel32.Hooks._createFile = (_, _, _, _, _, _, _) => FakeVolumeHandle.Create();

        using var handle = FileUtilities.Hooks._getVolumeHandle(@"\\.\C:");
        Assert.IsFalse(handle.IsInvalid);
    }

    // --- MftVolume.Open ---

    [TestMethod]
    public void Open_ValidVolume_ReturnsOpenVolume()
    {
        string? openedPath = null;
        FileUtilities.Hooks._getVolumeHandle = path =>
        {
            openedPath = path;
            return FakeVolumeHandle.Create();
        };
        using var volume = MftVolume.Open('c');
        Assert.IsNotNull(volume);
        Assert.AreEqual(@"\\.\C:", openedPath);

        FileUtilities.Hooks._getVolumeHandle = _ => throw new AssertFailedException("Default drive reached volume I/O.");
        Assert.ThrowsExactly<ArgumentException>(() => MftVolume.Open(default));
    }

    [TestMethod]
    public void Dispose_DisposesHandle()
    {
        SafeFileHandle handle;
        using (var volume = FakeVolumeHandle.OpenVolume('C', out handle))
        {
            Assert.IsFalse(handle.IsClosed);
            GC.KeepAlive(volume);
        }

        Assert.IsTrue(handle.IsClosed);
    }

    [TestMethod]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();

        var volume = MftVolume.Open('C');
        volume.Dispose();
        volume.Dispose(); // Should not throw
    }

    [TestMethod]
    public void Methods_AfterDispose_ThrowObjectDisposed()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();

        var volume = MftVolume.Open('C');
        volume.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => volume.ReadAll());
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            volume.ReadRecordBatches(4096, null, null, CancellationToken.None).ToList());
    }

    [TestMethod]
    public void ReadAll_ReturnsRecords()
    {
        SetupMocks();

        using var volume = MftVolume.Open('C');
        var records = volume.ReadAll();

        Assert.HasCount(3, records);
        Assert.AreEqual(0UL, records[0].RecordNumber);
        Assert.AreEqual("file0.txt", records[0].FileName);
    }

    // --- ReadRecordBatches ---

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReadRecordBatches_PassesIncludeFreedToTheNativeParser(bool includeFreed)
    {
        bool? requested = null;
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        MFTLibNative.Hooks._parseMftRecordsWithProgress = (_, freed, _, _, _) =>
        {
            requested = freed;
            return BuildResult(1);
        };
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        using var volume = MftVolume.Open('C');

        _ = volume.ReadRecordBatches(4096, null, null, CancellationToken.None,
            new MftScanOptions { IncludeFreed = includeFreed }).ToList();

        Assert.AreEqual(includeFreed, requested);
    }

    [TestMethod]
    public void MftResult_NativeCompactBytes_ComputesCorrectSize()
    {
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        using var stream = new MftResult(BuildResult(3));

        // 3 records * 52 bytes + string units (file0.txt=9, file1.txt=9, file2.txt=9 = 27 units * 2 bytes = 54)
        // 156 + 54 = 210 bytes
        Assert.AreEqual(210UL, stream.NativeCompactBytes);
    }

    [TestMethod]
    public void MftResult_Dispose_TotalsAndCompactBytesRemainReadable()
    {
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        var stream = new MftResult(BuildResult(3));
        stream.Dispose();

        Assert.AreEqual(3UL, stream.TotalRecords);
        Assert.AreEqual(3UL, stream.UsedRecords);
        Assert.AreEqual(210UL, stream.NativeCompactBytes);
    }

    // --- MftResult Error and Dispose ---

    [TestMethod]
    public void MftResult_ErrorMessage_ThrowsInvalidOperation()
    {
        var errorResultPtr = BuildResult(0, errorMessage: "Volume read failed");
        MFTLibNative.Hooks._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new MftResult(errorResultPtr));

        Assert.AreEqual("Volume read failed", ex.Message);
    }

    [TestMethod]
    public void MftResult_Dispose_FreesNativeResult()
    {
        var freed = false;
        var resultPtr = BuildResult(1);
        MFTLibNative.Hooks._freeMftResult = ptr =>
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
        MFTLibNative.Hooks._freeMftResult = ptr =>
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
    [DataRow(0u, false, DisplayName = "#700-deferred-empty-result")]
    [DataRow(0u, true, DisplayName = "#700-repeated-empty-result")]
    [DataRow(1u, false, DisplayName = "#700-deferred-nonempty-result")]
    [DataRow(1u, true, DisplayName = "#700-repeated-nonempty-result")]
    public void MftResult_EnumerationAfterDispose_ThrowsObjectDisposed(uint usedRecords, bool repeatEnumeration)
    {
        var resultPtr = BuildResult(usedRecords);
        MFTLibNative.Hooks._freeMftResult = _ => { };
        try
        {
            var result = new MftResult(resultPtr);
            PrepareBatches(result, repeatEnumeration, out var batches);
            result.Dispose();

            using var enumerator = batches.GetEnumerator();
            Assert.ThrowsExactly<ObjectDisposedException>(() => enumerator.MoveNext());
        }
        finally
        {
            FreeBuiltResult(resultPtr);
        }
    }

    [TestMethod]
    public void MftResult_ToArray_MaterializesAllRecords()
    {
        SetupMocks(5);

        using var volume = MftVolume.Open('C');
        var records = volume.ReadAll();

        Assert.HasCount(5, records);
        Assert.AreEqual("file0.txt", records[0].FileName);
        Assert.AreEqual("file4.txt", records[4].FileName);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-50)]
    [DataRow(-100)]
    public void MftResult_ReadBatches_ZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int batchSize)
    {
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        using var result = new MftResult(BuildResult(5));

        // Not enumerated: the batch size is rejected at the call.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => result.ReadBatches(batchSize));
    }

    [TestMethod]
    public void MftResult_ReadBatches_Disposed_ThrowsObjectDisposedException()
    {
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        var result = new MftResult(BuildResult(5));
        result.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            result.ReadBatches(4096).ToList());
    }

    [TestMethod]
    public void MftResult_ReadBatches_BatchesMatchRecordsInOrder()
    {
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        using var result = new MftResult(BuildResult(7));

        var batches = result.ReadBatches(3).ToList();

        Assert.HasCount(3, batches);
        Assert.HasCount(3, batches[0]);
        Assert.HasCount(3, batches[1]);
        Assert.HasCount(1, batches[2]);

        var concatenated = batches.SelectMany(b => b).ToArray();
        Assert.HasCount(7, concatenated);
        for (var i = 0; i < 7; i++)
        {
            Assert.AreEqual((ulong)i, concatenated[i].RecordNumber);
            Assert.AreEqual($"file{i}.txt", concatenated[i].FileName);
        }
    }

    [TestMethod]
    public void MftResult_ReadBatches_RecordsStayValidAfterResultDisposed()
    {
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        var result = new MftResult(BuildResult(3));
        var batches = result.ReadBatches(2).ToList();
        result.Dispose();

        Assert.HasCount(2, batches);
        Assert.AreEqual("file0.txt", batches[0][0].FileName);
        Assert.AreEqual("file1.txt", batches[0][1].FileName);
        Assert.AreEqual("file2.txt", batches[1][0].FileName);
    }

    [TestMethod]
    [DataRow(false, false, DisplayName = "call-after-dispose")]
    [DataRow(true, false, DisplayName = "#700-deferred-volume-enumeration")]
    [DataRow(true, true, DisplayName = "#700-repeated-volume-enumeration")]
    public void MftVolume_ReadRecordBatches_Disposed_ThrowsObjectDisposedException(
        bool readBeforeDispose, bool repeatEnumeration)
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        MFTLibNative.Hooks._parseMftRecordsWithProgress = (_, _, _, _, _) => BuildResult(1);
        MFTLibNative.Hooks._freeMftResult = FreeBuiltResult;
        var volume = MftVolume.Open('C');
        IEnumerable<MftRecord[]>? batches = null;
        if (readBeforeDispose)
        {
            batches = volume.ReadRecordBatches(4096, null, null, CancellationToken.None);
            if (repeatEnumeration)
            {
                Consume(batches);
            }
        }

        volume.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            (batches ?? volume.ReadRecordBatches(4096, null, null, CancellationToken.None)).ToList());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void MftVolume_ReadRecordBatches_ZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int batchSize)
    {
        SetupMocks(5);
        using var volume = MftVolume.Open('C');

        // Not enumerated: the batch size is rejected before the parse runs.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => volume.ReadRecordBatches(batchSize, null, null, CancellationToken.None));
    }

    [TestMethod]
    public void MftVolume_ReadRecordBatches_PreservesRecordOrderAcrossBatches()
    {
        SetupMocks(7);
        using var volume = MftVolume.Open('C');
        var batches = volume.ReadRecordBatches(3, null, null, CancellationToken.None).ToList();

        Assert.HasCount(3, batches);
        Assert.HasCount(3, batches[0]);
        Assert.HasCount(3, batches[1]);
        Assert.HasCount(1, batches[2]);

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
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var resultPtr = BuildResult(10);
        MFTLibNative.Hooks._parseMftRecordsWithProgress = (_, _, _, _, _) => resultPtr;
        MFTLibNative.Hooks._freeMftResult = ptr =>
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

        using var volume = MftVolume.Open('C');
        MftRecord[]? firstBatch = null;
        foreach (var batch in volume.ReadRecordBatches(3, null, null, CancellationToken.None))
        {
            firstBatch = batch;
            break;
        }

        Assert.IsTrue(freed, "Native MftResult should be freed upon early enumeration disposal");
        Assert.IsNotNull(firstBatch);
        Assert.HasCount(3, firstBatch);
        Assert.AreEqual("file0.txt", firstBatch[0].FileName);
    }

    [TestMethod]
    public unsafe void MftResult_RootDirectory_ZeroLengthName_ReadsAsDot()
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
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };

            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);

            MFTLibNative.Hooks._freeMftResult = _ => { };

            using var stream = new MftResult(resultPtr);
            var record = stream.ReadBatches(1).First()[0];

            Assert.AreEqual(".", record.FileName);
            Assert.IsTrue(record.IsDirectory);
            Assert.IsTrue(record.InUse);
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
