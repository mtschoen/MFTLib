using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public partial class MftResultTests
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
        MFTLibNative.NativeResetTestState();
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
    public void GetMftNativeAbiVersion_ReturnsVersion3()
    {
        var version = MFTLibNative._getMftNativeAbiVersion();
        Assert.AreEqual(3U, version);
    }

    [TestMethod]
    public void AbiStride_MatchesCancelledField()
    {
        var entryStrideOffset = (int)Marshal.OffsetOf<MftParseResult>(nameof(MftParseResult.EntryStride));
        var cancelledOffset = (int)Marshal.OffsetOf<MftParseResult>(nameof(MftParseResult.Cancelled));
        Assert.AreEqual(entryStrideOffset + sizeof(uint), cancelledOffset);
        Assert.AreEqual(cancelledOffset + sizeof(uint), Marshal.SizeOf<MftParseResult>());

        // A native cancelled result carries the stride and the cancelled flag exactly where the
        // managed layout reads them.
        var imagePath = Path.GetTempFileName();
        try
        {
            SyntheticNtfsImage.Write(imagePath, 64);
            MFTLibNative.NativeSetVolumeRecordSizeOverride(1024);
            using var control = new ParseControlBlock();
            control.RequestCancel();
            using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPtr = MFTLibNative._parseMftRecordsWithProgress(
                image.SafeFileHandle, null, MatchFlags.None, 64, control.Pointer, null);
            try
            {
                Assert.AreEqual((int)MFTLibNative.NativeCompactEntrySize, Marshal.ReadInt32(resultPtr, entryStrideOffset));
                Assert.AreEqual(1, Marshal.ReadInt32(resultPtr, cancelledOffset));
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPtr);
            }
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [TestMethod]
    public void MftResult_CancelledResult_ThrowsOperationCanceledCarryingTheToken()
    {
        var result = new MftParseResult
        {
            ErrorMessage = "Parse cancelled",
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize,
            Cancelled = 1
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        var freed = IntPtr.Zero;
        MFTLibNative._freeMftResult = pointer =>
        {
            freed = pointer;
            Marshal.FreeHGlobal(pointer);
        };
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        var exception = Assert.ThrowsException<OperationCanceledException>(() =>
            new MftResult(resultPtr, "C", token));

        Assert.AreEqual(token, exception.CancellationToken);
        Assert.AreEqual(resultPtr, freed, "A cancelled result must be freed before the throw");
    }

    [TestMethod]
    public void MftResult_AbiVersionMismatch_ThrowsInvalidOperation()
    {
        var result = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            AbiVersion = 999,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new MftResult(resultPtr, "C"));
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
            EntryStride = 32
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new MftResult(resultPtr, "C"));
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

            using var mftResult = new MftResult(resultPtr, "C");
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

            using var mftResult = new MftResult(resultPtr, "C");
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

            using var mftResult = new MftResult(resultPtr, "C");
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

            using var mftResult = new MftResult(resultPtr, "C");
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

            using var mftResult = new MftResult(resultPtr, "C");
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
}
