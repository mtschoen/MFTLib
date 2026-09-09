using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftResultTests
{
    string? _tempMftPath;

    [TestInitialize]
    public void Setup()
    {
        _tempMftPath = Path.GetTempFileName();
        MftVolume.GenerateSyntheticMFT(_tempMftPath, 500, 256);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        if (_tempMftPath != null && File.Exists(_tempMftPath))
        {
            File.Delete(_tempMftPath);
        }
    }

    [TestMethod]
    public void TotalRecords_ReturnsExpectedCount()
    {
        Assert.IsNotNull(_tempMftPath);
        MftVolume.ParseMFTFromFile(_tempMftPath, out var timings);
        Assert.AreEqual(500UL, timings.TotalRecords);
    }

    [TestMethod]
    public void UsedRecords_LessThanOrEqualToTotal()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out var timings);
        // UsedRecords excludes deleted/extension records
        Assert.IsTrue((ulong)records.Length <= timings.TotalRecords);
    }

    [TestMethod]
    public void ToArray_MaterializesRecords_StableStrings()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);

        // After ToArray, all records should have stable materialized strings
        foreach (var record in records)
        {
            var name1 = record.FileName;
            var name2 = record.FileName;
            Assert.AreEqual(name1, name2);
            Assert.IsNotNull(name1);
        }
    }

    [TestMethod]
    public void ToArray_WithPaths_MaterializesFullPaths()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, null, MatchFlags.ResolvePaths, out _);

        var withPaths = records.Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0);

        // FileName should be extractable from FullPath for path-resolved records
        foreach (var record in withPaths.Take(20))
        {
            if (record.RecordNumber == 5)
            {
                Assert.AreEqual(".", record.FileName);
                Assert.AreEqual(@"\", record.FullPath);
                continue;
            }

            var pathFileName = record.FullPath!.Contains('\\')
                ? record.FullPath[(record.FullPath.LastIndexOf('\\') + 1)..]
                : record.FullPath;
            Assert.AreEqual(pathFileName, record.FileName,
                $"FileName '{record.FileName}' doesn't match end of FullPath '{record.FullPath}'");
        }
    }

    [TestMethod]
    public void GetMftNativeAbiVersion_ReturnsVersion4()
    {
        var version = MFTLibNative._getMftNativeAbiVersion();
        Assert.AreEqual(4U, version);
    }

    [TestMethod]
    public void MftResult_AbiVersionMismatch_ThrowsInvalidOperation()
    {
        var result = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            AbiVersion = 1, // Mismatch (expected 4)
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new MftResult(resultPtr, "C", 0));
        Assert.IsTrue(ex.Message.Contains("ABI mismatch"));
    }

    [TestMethod]
    public void MftResult_EntryStrideMismatch_ThrowsInvalidOperation()
    {
        var result = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = 32 // Mismatch (expected 48)
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new MftResult(resultPtr, "C", 0));
        Assert.IsTrue(ex.Message.Contains("stride"));
    }

    [TestMethod]
    public unsafe void MftResult_StringOffsetOutOfBounds_ThrowsInvalidDataException()
    {
        var entryBuf = (IntPtr)NativeMemory.AllocZeroed(MFTLibNative.NativeCompactEntrySize);
        var stringBuf = (IntPtr)NativeMemory.AllocZeroed(10 * sizeof(char));
        try
        {
            var ptr = (byte*)entryBuf;
            Unsafe.WriteUnaligned(ptr, 100UL); // recordNumber
            Unsafe.WriteUnaligned(ptr + 8, 5UL); // parentRecordNumber
            Unsafe.WriteUnaligned(ptr + 16, 20UL); // stringOffset > poolUnits (20 > 10)
            Unsafe.WriteUnaligned(ptr + 24, 0U);
            Unsafe.WriteUnaligned(ptr + 28, (ushort)1);
            Unsafe.WriteUnaligned(ptr + 30, (ushort)0);

            var result = new MftParseResult
            {
                TotalRecords = 1,
                UsedRecords = 1,
                Entries = entryBuf,
                EntryStrings = stringBuf,
                EntryStringUnits = 10,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);
            MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

            using var mftResult = new MftResult(resultPtr, "C", 0);
            var ex = Assert.ThrowsException<InvalidDataException>(mftResult.ToArray);
            Assert.AreEqual("Native MFT string offset is outside its pool", ex.Message);
        }
        finally
        {
            NativeMemory.Free((void*)entryBuf);
            NativeMemory.Free((void*)stringBuf);
        }
    }

    [TestMethod]
    public unsafe void MftResult_StringLengthOverflowsPool_ThrowsInvalidDataException()
    {
        var entryBuf = (IntPtr)NativeMemory.AllocZeroed(MFTLibNative.NativeCompactEntrySize);
        var stringBuf = (IntPtr)NativeMemory.AllocZeroed(10 * sizeof(char));
        try
        {
            var ptr = (byte*)entryBuf;
            Unsafe.WriteUnaligned(ptr, 100UL);
            Unsafe.WriteUnaligned(ptr + 8, 5UL);
            Unsafe.WriteUnaligned(ptr + 16, 8UL); // stringOffset = 8
            Unsafe.WriteUnaligned(ptr + 24, 0U);
            Unsafe.WriteUnaligned(ptr + 28, (ushort)1);
            Unsafe.WriteUnaligned(ptr + 30, (ushort)5); // length 5 -> 8 + 5 = 13 > 10

            var result = new MftParseResult
            {
                TotalRecords = 1,
                UsedRecords = 1,
                Entries = entryBuf,
                EntryStrings = stringBuf,
                EntryStringUnits = 10,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);
            MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

            using var mftResult = new MftResult(resultPtr, "C", 0);
            var ex = Assert.ThrowsException<InvalidDataException>(mftResult.ToArray);
            Assert.AreEqual("Native MFT string offset is outside its pool", ex.Message);
        }
        finally
        {
            NativeMemory.Free((void*)entryBuf);
            NativeMemory.Free((void*)stringBuf);
        }
    }

    [TestMethod]
    public unsafe void MftResult_ZeroLengthStringAtPoolEnd_SucceedsWithEmptyString()
    {
        var entryBuf = (IntPtr)NativeMemory.AllocZeroed(MFTLibNative.NativeCompactEntrySize);
        var stringBuf = (IntPtr)NativeMemory.AllocZeroed(10 * sizeof(char));
        try
        {
            var ptr = (byte*)entryBuf;
            Unsafe.WriteUnaligned(ptr, 100UL);
            Unsafe.WriteUnaligned(ptr + 8, 5UL);
            Unsafe.WriteUnaligned(ptr + 16, 10UL); // stringOffset == poolUnits
            Unsafe.WriteUnaligned(ptr + 24, 0U);
            Unsafe.WriteUnaligned(ptr + 28, (ushort)1);
            Unsafe.WriteUnaligned(ptr + 30, (ushort)0); // stringLength == 0

            var result = new MftParseResult
            {
                TotalRecords = 1,
                UsedRecords = 1,
                Entries = entryBuf,
                EntryStrings = stringBuf,
                EntryStringUnits = 10,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);
            MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

            using var mftResult = new MftResult(resultPtr, "C", 0);
            var records = mftResult.ToArray();
            Assert.AreEqual(1, records.Length);
            Assert.AreEqual(string.Empty, records[0].FileName);
        }
        finally
        {
            NativeMemory.Free((void*)entryBuf);
            NativeMemory.Free((void*)stringBuf);
        }
    }

    [TestMethod]
    public unsafe void MftResult_LongPathOver1024Units_MaterializesFully()
    {
        var longPath = new string('a', 1500);
        var entryBuf = (IntPtr)NativeMemory.AllocZeroed(MFTLibNative.NativeCompactEntrySize);
        var stringBuf = (IntPtr)NativeMemory.AllocZeroed((nuint)(longPath.Length * sizeof(char)));
        try
        {
            longPath.AsSpan().CopyTo(new Span<char>((void*)stringBuf, longPath.Length));

            var ptr = (byte*)entryBuf;
            Unsafe.WriteUnaligned(ptr, 100UL);
            Unsafe.WriteUnaligned(ptr + 8, 5UL);
            Unsafe.WriteUnaligned(ptr + 16, 0UL);
            Unsafe.WriteUnaligned(ptr + 24, 0U);
            Unsafe.WriteUnaligned(ptr + 28, (ushort)1);
            Unsafe.WriteUnaligned(ptr + 30, (ushort)longPath.Length);

            var result = new MftParseResult
            {
                TotalRecords = 1,
                UsedRecords = 1,
                PathEntries = entryBuf,
                PathStrings = stringBuf,
                PathStringUnits = (ulong)longPath.Length,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);
            MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

            using var mftResult = new MftResult(resultPtr, "C", 0);
            var records = mftResult.ToArray();
            Assert.AreEqual(1, records.Length);
            Assert.AreEqual($"C:\\{longPath}", records[0].FullPath);
            Assert.AreEqual(1503, records[0].FullPath!.Length);
        }
        finally
        {
            NativeMemory.Free((void*)entryBuf);
            NativeMemory.Free((void*)stringBuf);
        }
    }

    [TestMethod]
    public unsafe void MftResult_PathAllocationFailureFallback_PreservesRawEntries()
    {
        var fileName = "fallback_file.txt";
        var entryBuf = (IntPtr)NativeMemory.AllocZeroed(MFTLibNative.NativeCompactEntrySize);
        var stringBuf = (IntPtr)NativeMemory.AllocZeroed((nuint)(fileName.Length * sizeof(char)));
        try
        {
            fileName.AsSpan().CopyTo(new Span<char>((void*)stringBuf, fileName.Length));

            var ptr = (byte*)entryBuf;
            Unsafe.WriteUnaligned(ptr, 100UL);
            Unsafe.WriteUnaligned(ptr + 8, 5UL);
            Unsafe.WriteUnaligned(ptr + 16, 0UL);
            Unsafe.WriteUnaligned(ptr + 24, 0U);
            Unsafe.WriteUnaligned(ptr + 28, (ushort)1);
            Unsafe.WriteUnaligned(ptr + 30, (ushort)fileName.Length);

            var result = new MftParseResult
            {
                TotalRecords = 1,
                UsedRecords = 1,
                Entries = entryBuf,
                EntryStrings = stringBuf,
                EntryStringUnits = (ulong)fileName.Length,
                PathEntries = IntPtr.Zero,
                PathStrings = IntPtr.Zero,
                PathStringUnits = 0,
                ErrorMessage = string.Empty,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);
            MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

            using var mftResult = new MftResult(resultPtr, "C", 0);
            var records = mftResult.ToArray();
            Assert.AreEqual(1, records.Length);
            Assert.AreEqual(fileName, records[0].FileName);
            Assert.IsNull(records[0].FullPath);
        }
        finally
        {
            NativeMemory.Free((void*)entryBuf);
            NativeMemory.Free((void*)stringBuf);
        }
    }

    [TestMethod]
    public void MftVolume_EnsureCompatibleNativeAbi_ThrowsOnMismatch()
    {
        MFTLibNative._getMftNativeAbiVersion = () => 999;

        var ex = Assert.ThrowsException<InvalidOperationException>(MFTLibNative.EnsureCompatibleNativeAbi);
        Assert.IsTrue(ex.Message.Contains("ABI mismatch"));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-100)]
    public void MaterializeBatches_BatchSizeZeroOrNegative_ThrowsArgumentOutOfRangeException(int batchSize)
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
        {
            using var result = new MftResult(resultPtr, string.Empty, 0);
            _ = result.MaterializeBatches(batchSize).ToList();
        });
    }

    [TestMethod]
    public void MaterializeBatches_Disposed_ThrowsObjectDisposedException()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256);
        var result = new MftResult(resultPtr, string.Empty, 0);
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() =>
            result.MaterializeBatches().ToList());
    }

    [TestMethod]
    public void MaterializeBatches_DefaultBatchSize_BatchesHaveExpectedLengthsAndOrder()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256);
        using var result = new MftResult(resultPtr, string.Empty, 0);

        var expectedRecordNumbers = result.Select(r => r.RecordNumber).ToArray();
        var batches = result.MaterializeBatches().ToList();

        foreach (var batch in batches)
        {
            Assert.IsTrue(batch.Length > 0 && batch.Length <= 4096);
        }

        var actualRecordNumbers = batches.SelectMany(b => b).Select(r => r.RecordNumber).ToArray();
        CollectionAssert.AreEqual(expectedRecordNumbers, actualRecordNumbers);
    }

    [TestMethod]
    public void MaterializeBatches_CustomBatchSize_BatchesMatchRecordsInOrder()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256);
        using var result = new MftResult(resultPtr, string.Empty, 0);

        const int batchSize = 64;
        var batches = result.MaterializeBatches(batchSize).ToList();

        Assert.IsTrue(batches.Count > 1);
        for (var i = 0; i < batches.Count - 1; i++)
        {
            Assert.AreEqual(batchSize, batches[i].Length);
        }

        Assert.IsTrue(batches[^1].Length <= batchSize);

        var expectedRecordNumbers = result.Select(r => r.RecordNumber).ToArray();
        var actualRecordNumbers = batches.SelectMany(b => b).Select(r => r.RecordNumber).ToArray();
        CollectionAssert.AreEqual(expectedRecordNumbers, actualRecordNumbers);
    }

    [TestMethod]
    public void MaterializeBatches_WithPaths_MaterializesFullPaths()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.ResolvePaths, 256);
        using var result = new MftResult(resultPtr, "C", 0);

        var batches = result.MaterializeBatches(50).ToList();
        var withPaths = batches.SelectMany(b => b).Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0);
    }

    [TestMethod]
    public void MaterializeBatches_RecordsStayValidAfterNextBatch()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256);
        using var result = new MftResult(resultPtr, string.Empty, 0);

        using var enumerator = result.MaterializeBatches(10).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        var firstBatch = enumerator.Current;
        var firstBatchNames = firstBatch.Select(r => r.FileName).ToArray();

        Assert.IsTrue(enumerator.MoveNext());
        var secondBatch = enumerator.Current;
        Assert.IsTrue(secondBatch.Length > 0);

        for (var i = 0; i < firstBatch.Length; i++)
        {
            Assert.AreEqual(firstBatchNames[i], firstBatch[i].FileName);
        }
    }

    [TestMethod]
    public void ToArray_MatchesMaterializeBatches()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256);
        using var result = new MftResult(resultPtr, string.Empty, 0);
        var expectedRecords = result.ToArray();

        var batches = result.MaterializeBatches(128).ToList();
        var actualRecords = batches.SelectMany(b => b).ToArray();

        Assert.AreEqual(expectedRecords.Length, actualRecords.Length);
        for (var i = 0; i < expectedRecords.Length; i++)
        {
            Assert.AreEqual(expectedRecords[i].RecordNumber, actualRecords[i].RecordNumber);
            Assert.AreEqual(expectedRecords[i].FileName, actualRecords[i].FileName);
        }
    }
}
