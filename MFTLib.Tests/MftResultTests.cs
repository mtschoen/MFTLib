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
    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.ResetToDefaults();
    }

    [TestMethod]
    public void AbiStride_MatchesCancelledField()
    {
        var entryStrideOffset = (int)Marshal.OffsetOf<MftParseResult>(nameof(MftParseResult.EntryStride));
        var cancelledOffset = (int)Marshal.OffsetOf<MftParseResult>(nameof(MftParseResult.Cancelled));
        var invalidInputOffset = (int)Marshal.OffsetOf<MftParseResult>(nameof(MftParseResult.InvalidInput));
        Assert.AreEqual(entryStrideOffset + sizeof(uint), cancelledOffset);
        Assert.AreEqual(cancelledOffset + sizeof(uint), invalidInputOffset);
        var invalidFixupOffset = (int)Marshal.OffsetOf<MftParseResult>(nameof(MftParseResult.InvalidFixupRecords));
        Assert.AreEqual(invalidInputOffset + sizeof(uint), invalidFixupOffset);
        Assert.AreEqual(invalidFixupOffset + sizeof(ulong), Marshal.SizeOf<MftParseResult>());

        // A native cancelled result carries the stride and the cancelled flag exactly where the
        // managed layout reads them.
        var imagePath = Path.GetTempFileName();
        try
        {
            SyntheticNtfsImage.Write(imagePath, 64);
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
            using var control = new ParseControlBlock();
            control.RequestCancel();
            using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPtr = MFTLibNative._parseMftRecordsWithProgress(
                image.SafeFileHandle, false, 64, control.Pointer, null);
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
            new MftResult(resultPtr, token));

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
            new MftResult(resultPtr));
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
            new MftResult(resultPtr));
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

            using var mftResult = new MftResult(resultPtr);
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

            using var mftResult = new MftResult(resultPtr);
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

            using var mftResult = new MftResult(resultPtr);
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
}
