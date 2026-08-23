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

    static unsafe IntPtr BuildResult(uint usedRecords, bool withPaths = false, string? errorMessage = null)
    {
        var entryBufSize = (int)(MFTLibNative.NativeCompactEntrySize * usedRecords);
        var entryBuf = Marshal.AllocHGlobal(entryBufSize);
        new Span<byte>((void*)entryBuf, entryBufSize).Clear();

        var strings = new List<string>();
        var totalStringUnits = 0;
        for (uint i = 0; i < usedRecords; i++)
        {
            var str = withPaths ? $"dir\\file{i}.txt" : $"file{i}.txt";
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
            Entries = withPaths ? IntPtr.Zero : entryBuf,
            EntryStrings = withPaths ? IntPtr.Zero : stringBuf,
            EntryStringUnits = withPaths ? 0 : (ulong)totalStringUnits,
            PathEntries = withPaths ? entryBuf : IntPtr.Zero,
            PathStrings = withPaths ? stringBuf : IntPtr.Zero,
            PathStringUnits = withPaths ? (ulong)totalStringUnits : 0,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize,
            ErrorMessage = errorMessage ?? string.Empty
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    static void SetupMocks(uint usedRecords = 3, bool withPaths = false)
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        var resultPtr = BuildResult(usedRecords, withPaths);
        MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = ptr =>
        {
            var parseResult = Marshal.PtrToStructure<MftParseResult>(ptr);
            var entryBuf = parseResult.Entries != IntPtr.Zero ? parseResult.Entries : parseResult.PathEntries;
            if (entryBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entryBuf);
            }

            var stringBuf = parseResult.EntryStrings != IntPtr.Zero
                ? parseResult.EntryStrings
                : parseResult.PathStrings;
            if (stringBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(stringBuf);
            }

            Marshal.FreeHGlobal(ptr);
        };
    }

    // --- FileUtilities ---

    [TestMethod]
    public void GetVolumePath_DriveLetter_ReturnsNormalizedPath()
    {
        Assert.AreEqual(@"\\.\C:", MFTUtilities.GetVolumePath("C"));
        Assert.AreEqual(@"\\.\C:", MFTUtilities.GetVolumePath("C:"));
        Assert.AreEqual(@"\\.\C:", MFTUtilities.GetVolumePath(@"C:\"));
        Assert.AreEqual(@"\\.\C:", MFTUtilities.GetVolumePath(@"\\.\C:"));
    }

    [TestMethod]
    public void GetVolumePath_NullOrEmpty_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => MFTUtilities.GetVolumePath(null!));
        Assert.ThrowsException<ArgumentNullException>(() => MFTUtilities.GetVolumePath(string.Empty));
        Assert.ThrowsException<ArgumentException>(() => MFTUtilities.GetVolumePath("   "));
    }

    [TestMethod]
    public void GetVolumePath_InvalidFormat_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() => MFTUtilities.GetVolumePath("invalid_path"));
    }

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
        var handle = FakeHandle();
        FileUtilities._getVolumeHandle = _ => handle;

        var volume = MftVolume.Open("C");
        volume.Dispose();

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

        Assert.ThrowsException<ObjectDisposedException>(volume.ReadAllRecords);
        Assert.ThrowsException<ObjectDisposedException>(() => volume.FindByName("test"));
        Assert.ThrowsException<ObjectDisposedException>(() => volume.StreamRecords());
        Assert.ThrowsException<ObjectDisposedException>(() => volume.FindDirectories("test").ToList());
        Assert.ThrowsException<ObjectDisposedException>(() => volume.FindFiles("test").ToList());
        Assert.ThrowsException<ObjectDisposedException>(() => volume.FindRecords("test").ToList());
    }

    // --- ReadAllRecords ---

    [TestMethod]
    public void ReadAllRecords_NoPaths_ReturnsRecords()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords();

        Assert.AreEqual(3, records.Length);
        Assert.AreEqual(0UL, records[0].RecordNumber);
        Assert.AreEqual("file0.txt", records[0].FileName);
        Assert.IsNull(records[0].FullPath);
    }

    [TestMethod]
    public void ReadAllRecords_WithPaths_ReturnsRecordsWithFullPaths()
    {
        SetupMocks(withPaths: true);

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(true);

        Assert.AreEqual(3, records.Length);
        Assert.AreEqual(@"C:\dir\file0.txt", records[0].FullPath);
        Assert.AreEqual("file0.txt", records[0].FileName);
    }

    [TestMethod]
    public void ReadAllRecords_WithTimings_PopulatesTimings()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(out var timings);

        Assert.AreEqual(3, records.Length);
        Assert.AreEqual(3UL, timings.TotalRecords);
        Assert.IsTrue(timings.MarshalMs >= 0);
    }

    [TestMethod]
    public void ReadAllRecords_WithPathsAndTimings_PopulatesBoth()
    {
        SetupMocks(withPaths: true);

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(true, out var timings);

        Assert.AreEqual(3, records.Length);
        Assert.AreEqual(@"C:\dir\file0.txt", records[0].FullPath);
        Assert.AreEqual(3UL, timings.TotalRecords);
    }

    // --- FindByName ---

    [TestMethod]
    public void FindByName_DefaultFlags_PassesExactMatch()
    {
        MatchFlags capturedFlags = 0;
        string? capturedFilter = null;

        FileUtilities._getVolumeHandle = _ => FakeHandle();
        MFTLibNative._parseMftRecords = (_, filter, flags, _) =>
        {
            capturedFilter = filter;
            capturedFlags = flags;
            return BuildResult(1);
        };
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

        using var volume = MftVolume.Open("C");
        var records = volume.FindByName("test.txt");

        Assert.AreEqual(1, records.Length);
        Assert.AreEqual("test.txt", capturedFilter);
        Assert.AreEqual(MatchFlags.ExactMatch, capturedFlags);
    }

    [TestMethod]
    public void FindByName_WithTimings_PopulatesTimings()
    {
        SetupMocks(2);

        using var volume = MftVolume.Open("C");
        var records = volume.FindByName("file", MatchFlags.Contains, out var timings);

        Assert.AreEqual(2, records.Length);
        Assert.AreEqual(2UL, timings.TotalRecords);
    }

    // --- StreamRecords ---

    [TestMethod]
    public void StreamRecords_ReturnsEnumerableStream()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        using var stream = volume.StreamRecords();

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
        using var stream = volume.StreamRecords();

        IEnumerable nonGeneric = stream;
        var count = 0;
        foreach (var item in nonGeneric)
        {
            Assert.IsInstanceOfType<MftRecord>(item);
            count++;
        }

        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public void MftResult_NativeCompactBytes_WithoutPaths_ComputesCorrectSize()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        using var stream = volume.StreamRecords();

        // 3 records * 32 bytes + string units (file0.txt=9, file1.txt=9, file2.txt=9 = 27 units * 2 bytes = 54)
        // 96 + 54 = 150 bytes
        Assert.AreEqual(150UL, stream.NativeCompactBytes);
    }

    [TestMethod]
    public void MftResult_NativeCompactBytes_WithPaths_ComputesCorrectSize()
    {
        SetupMocks(3, true);

        using var volume = MftVolume.Open("C");
        using var stream = volume.StreamRecords();

        // With paths: pathEntries (3*32 = 96) + pathStrings (dir\file0.txt=13, 13, 13 = 39 units * 2 bytes = 78)
        // 96 + 78 = 174 bytes
        Assert.AreEqual(174UL, stream.NativeCompactBytes);
    }

    [TestMethod]
    public void MftResult_Dispose_TotalsAndCompactBytesRemainReadable()
    {
        SetupMocks();

        using var volume = MftVolume.Open("C");
        var stream = volume.StreamRecords();
        stream.Dispose();

        Assert.AreEqual(3UL, stream.TotalRecords);
        Assert.AreEqual(3UL, stream.UsedRecords);
        Assert.AreEqual(150UL, stream.NativeCompactBytes);
        Assert.IsNotNull(stream.Timings);
    }

    // --- FindFiles, FindDirectories, FindRecords ---

    [TestMethod]
    public unsafe void FindFiles_ReturnsOnlyFiles()
    {
        var entryBufSize = (int)(2 * MFTLibNative.NativeCompactEntrySize);
        var entryBuf = Marshal.AllocHGlobal(entryBufSize);
        new Span<byte>((void*)entryBuf, entryBufSize).Clear();

        var path1 = "test.txt";
        var path2 = "somedir";
        var totalUnits = path1.Length + path2.Length;
        var stringBuf = Marshal.AllocHGlobal(totalUnits * sizeof(char));
        var stringSpan = new Span<char>((void*)stringBuf, totalUnits);
        path1.AsSpan().CopyTo(stringSpan);
        path2.AsSpan().CopyTo(stringSpan.Slice(path1.Length));

        // File entry
        var ptr = (byte*)entryBuf;
        Unsafe.WriteUnaligned(ptr, 0UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, 0UL); // stringOffset = 0
        Unsafe.WriteUnaligned(ptr + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(ptr + 28, (ushort)1); // InUse, not directory
        Unsafe.WriteUnaligned(ptr + 30, (ushort)path1.Length);

        // Directory entry
        ptr = (byte*)entryBuf + MFTLibNative.NativeCompactEntrySize;
        Unsafe.WriteUnaligned(ptr, 1UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, (ulong)path1.Length); // stringOffset = 8
        Unsafe.WriteUnaligned(ptr + 24, (uint)FileAttributes.Directory);
        Unsafe.WriteUnaligned(ptr + 28, (ushort)3); // InUse + Directory
        Unsafe.WriteUnaligned(ptr + 30, (ushort)path2.Length);

        var result = new MftParseResult
        {
            TotalRecords = 2,
            UsedRecords = 2,
            PathEntries = entryBuf,
            PathStrings = stringBuf,
            PathStringUnits = (ulong)totalUnits,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);

        FileUtilities._getVolumeHandle = _ => FakeHandle();
        MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = p =>
        {
            var r = Marshal.PtrToStructure<MftParseResult>(p);
            if (r.PathEntries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(r.PathEntries);
            }

            if (r.PathStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(r.PathStrings);
            }

            Marshal.FreeHGlobal(p);
        };

        using var volume = MftVolume.Open("C");
        var files = volume.FindFiles("test.txt").ToList();

        Assert.AreEqual(1, files.Count);
        Assert.IsTrue(files[0].EndsWith("test.txt", StringComparison.Ordinal));
    }

    [TestMethod]
    public unsafe void FindDirectories_ReturnsOnlyDirectories()
    {
        var entryBufSize = (int)(2 * MFTLibNative.NativeCompactEntrySize);
        var entryBuf = Marshal.AllocHGlobal(entryBufSize);
        new Span<byte>((void*)entryBuf, entryBufSize).Clear();

        var path1 = "test.txt";
        var path2 = "somedir";
        var totalUnits = path1.Length + path2.Length;
        var stringBuf = Marshal.AllocHGlobal(totalUnits * sizeof(char));
        var stringSpan = new Span<char>((void*)stringBuf, totalUnits);
        path1.AsSpan().CopyTo(stringSpan);
        path2.AsSpan().CopyTo(stringSpan.Slice(path1.Length));

        // File entry
        var ptr = (byte*)entryBuf;
        Unsafe.WriteUnaligned(ptr, 0UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, 0UL);
        Unsafe.WriteUnaligned(ptr + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(ptr + 28, (ushort)1);
        Unsafe.WriteUnaligned(ptr + 30, (ushort)path1.Length);

        // Directory entry
        ptr = (byte*)entryBuf + MFTLibNative.NativeCompactEntrySize;
        Unsafe.WriteUnaligned(ptr, 1UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, (ulong)path1.Length);
        Unsafe.WriteUnaligned(ptr + 24, (uint)FileAttributes.Directory);
        Unsafe.WriteUnaligned(ptr + 28, (ushort)3);
        Unsafe.WriteUnaligned(ptr + 30, (ushort)path2.Length);

        var result = new MftParseResult
        {
            TotalRecords = 2,
            UsedRecords = 2,
            PathEntries = entryBuf,
            PathStrings = stringBuf,
            PathStringUnits = (ulong)totalUnits,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);

        FileUtilities._getVolumeHandle = _ => FakeHandle();
        MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = p =>
        {
            var r = Marshal.PtrToStructure<MftParseResult>(p);
            if (r.PathEntries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(r.PathEntries);
            }

            if (r.PathStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(r.PathStrings);
            }

            Marshal.FreeHGlobal(p);
        };

        using var volume = MftVolume.Open("C");
        var directories = volume.FindDirectories("somedir").ToList();

        Assert.AreEqual(1, directories.Count);
        Assert.IsTrue(directories[0].EndsWith("somedir", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FindRecords_NullDirectoryFilter_ReturnsBoth()
    {
        SetupMocks(withPaths: true);

        using var volume = MftVolume.Open("C");
        var all = volume.FindRecords("file").ToList();

        Assert.AreEqual(3, all.Count);
    }

    [TestMethod]
    public void FindRecords_IgnoresRecordsWithoutFullPath()
    {
        // Setup without paths - FullPath will be null, but Fallback will yield FileName
        SetupMocks();

        using var volume = MftVolume.Open("C");
        var results = volume.FindRecords("file").ToList();

        Assert.AreEqual(3, results.Count);
    }

    // --- ExtractDriveLetter ---

    [TestMethod]
    public void ExtractDriveLetter_VariousInputs_ReturnsCorrectLetter()
    {
        Assert.AreEqual("C", MftVolume.ExtractDriveLetter(@"\\.\C:"));
        Assert.AreEqual("D", MftVolume.ExtractDriveLetter(@"\\.\D:"));
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter("C:"));
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(@"\\.\Volume{123}"));
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(string.Empty));
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(@"\\.\"));
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(@"\\.\C/"));
    }

    // --- ParseMFTFromFile ---

    [TestMethod]
    public void ParseMFTFromFile_WithTimings_ReturnsRecordsAndTimings()
    {
        MFTLibNative._parseMftFromFile = (_, _, _, _) => BuildResult(2);
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

        var records = MftVolume.ParseMFTFromFile("fake.bin", out var timings);

        Assert.AreEqual(2, records.Length);
        Assert.AreEqual(2UL, timings.TotalRecords);
    }

    [TestMethod]
    public void StreamMFTFromFile_ReturnsStream()
    {
        MFTLibNative._parseMftFromFile = (_, _, _, _) => BuildResult(3);
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

        using var result = MftVolume.StreamMFTFromFile("fake.bin");

        Assert.AreEqual(3UL, result.TotalRecords);
        Assert.AreEqual(3UL, result.UsedRecords);
    }

    [TestMethod]
    public void StreamMFTFromFile_NullReturn_ThrowsInvalidOperation()
    {
        MFTLibNative._parseMftFromFile = (_, _, _, _) => IntPtr.Zero;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.StreamMFTFromFile("fake.bin"));
    }

    // --- MftResult Error and Dispose ---

    [TestMethod]
    public void MftResult_ErrorMessage_ThrowsInvalidOperation()
    {
        var errorResultPtr = BuildResult(0, errorMessage: "Volume read failed");
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new MftResult(errorResultPtr, "C", 0));

        Assert.AreEqual("Volume read failed", ex.Message);
    }

    [TestMethod]
    public void MftResult_AbiVersionMismatch_ThrowsInvalidOperation()
    {
        var result = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            AbiVersion = 1,
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
            EntryStride = 40
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new MftResult(resultPtr, "C", 0));
        Assert.IsTrue(ex.Message.Contains("stride"));
    }

    [TestMethod]
    public void MftResult_Enumerate_WithPaths_ReadsRecords()
    {
        SetupMocks(withPaths: true);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

        var records = new List<MftRecord>();
        foreach (var record in result)
        {
            records.Add(record.Materialize());
        }

        Assert.AreEqual(3, records.Count);
        Assert.AreEqual(@"C:\dir\file0.txt", records[0].FullPath);
    }

    [TestMethod]
    public void MftVolume_GetVolumeHandleForTest_ReturnsHandle()
    {
        var handle = FakeHandle();
        FileUtilities._getVolumeHandle = _ => handle;

        using var volume = MftVolume.Open("C");
        Assert.AreSame(handle, volume.GetVolumeHandleForTest());
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

        var result = new MftResult(resultPtr, "C", 0);
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

        var result = new MftResult(resultPtr, "C", 0);
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

        var result = new MftResult(resultPtr, "C", 0);
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(result.GetEnumerator);
        Assert.ThrowsException<ObjectDisposedException>(result.ToArray);
    }

    [TestMethod]
    public void MftResult_ToArray_MaterializesAllRecords()
    {
        SetupMocks(5);

        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords();

        Assert.AreEqual(5, records.Length);
        Assert.AreEqual("file0.txt", records[0].FileName);
        Assert.AreEqual("file4.txt", records[4].FileName);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-50)]
    public void MftResult_MaterializeBatches_ZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int batchSize)
    {
        SetupMocks(5);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
        {
            using var volume = MftVolume.Open("C");
            using var result = volume.StreamRecords();
            _ = result.MaterializeBatches(batchSize).ToList();
        });
    }

    [TestMethod]
    public void MftResult_MaterializeBatches_Disposed_ThrowsObjectDisposedException()
    {
        SetupMocks(5);
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords();
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() =>
            result.MaterializeBatches().ToList());
    }

    [TestMethod]
    public void MftResult_MaterializeBatches_BatchesMatchRecordsInOrder()
    {
        SetupMocks(7);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

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
    public void MftResult_MaterializeBatches_WithPaths_MaterializesFullPaths()
    {
        SetupMocks(5, true);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

        var batches = result.MaterializeBatches(2).ToList();

        Assert.AreEqual(3, batches.Count);
        var concatenated = batches.SelectMany(b => b).ToArray();
        Assert.AreEqual(5, concatenated.Length);
        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual($@"C:\dir\file{i}.txt", concatenated[i].FullPath);
            Assert.AreEqual($"file{i}.txt", concatenated[i].FileName);
        }
    }

    [TestMethod]
    public void MftResult_MaterializeBatches_RecordsStayValidAfterResultDisposed()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords();
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
            volume.ReadRecordBatches().ToList());
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
            _ = volume.ReadRecordBatches(batchSize: batchSize).ToList();
        });
    }

    [TestMethod]
    public void MftVolume_ReadRecordBatches_BatchesMatchReadAllRecords()
    {
        SetupMocks(7);
        using var volume = MftVolume.Open("C");
        var batches = volume.ReadRecordBatches(batchSize: 3).ToList();

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
    public void MftVolume_ReadRecordBatches_WithResolvePaths_PopulatesFullPaths()
    {
        SetupMocks(4, true);
        using var volume = MftVolume.Open("C");
        var batches = volume.ReadRecordBatches(resolvePaths: true, 2).ToList();

        Assert.AreEqual(2, batches.Count);
        var concatenated = batches.SelectMany(b => b).ToArray();
        Assert.AreEqual(4, concatenated.Length);
        for (var i = 0; i < 4; i++)
        {
            Assert.AreEqual($@"C:\dir\file{i}.txt", concatenated[i].FullPath);
            Assert.AreEqual($"file{i}.txt", concatenated[i].FileName);
        }
    }

    [TestMethod]
    public void MftVolume_ReadRecordBatches_EarlyEnumerationDisposal_FreesNativeResult()
    {
        var freed = false;
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        var resultPtr = BuildResult(10);
        MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
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
        foreach (var batch in volume.ReadRecordBatches(batchSize: 3))
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
    public unsafe void MftRecord_FileName_ExtractedFromPathWhenNoNamePointer()
    {
        var compactSize = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = (IntPtr)NativeMemory.AllocZeroed(compactSize);
        var path = "dir\\file.txt";
        var stringBuf = (IntPtr)NativeMemory.AllocZeroed((nuint)(path.Length * sizeof(char)));
        try
        {
            path.AsSpan().CopyTo(new Span<char>((void*)stringBuf, path.Length));

            var ptr = (byte*)entryBuf;
            Unsafe.WriteUnaligned(ptr, 0UL); // recordNumber
            Unsafe.WriteUnaligned(ptr + 8, 5UL); // parentRecordNumber
            Unsafe.WriteUnaligned(ptr + 16, 0UL); // stringOffset
            Unsafe.WriteUnaligned(ptr + 24, (uint)FileAttributes.Normal);
            Unsafe.WriteUnaligned(ptr + 28, (ushort)1); // flags = InUse
            Unsafe.WriteUnaligned(ptr + 30, (ushort)path.Length);

            var result = new MftParseResult
            {
                TotalRecords = 1,
                UsedRecords = 1,
                PathEntries = entryBuf,
                PathStrings = stringBuf,
                PathStringUnits = (ulong)path.Length,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };

            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);

            FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
            MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
            MFTLibNative._freeMftResult = _ => { };

            using var volume = MftVolume.Open("T");
            using var stream = volume.StreamRecords();
            var record = stream.First();

            Assert.AreEqual("file.txt", record.FileName);
            Assert.AreEqual("T:\\dir\\file.txt", record.FullPath);
        }
        finally
        {
            NativeMemory.Free((void*)entryBuf);
            NativeMemory.Free((void*)stringBuf);
        }
    }

    [TestMethod]
    public void MftRecord_FullPath_NoDriveLetter_ReturnsRelativePath()
    {
        var record = new MftRecord(0, 5, 1, "file.txt", "some\\path\\file.txt");
        Assert.AreEqual("some\\path\\file.txt", record.FullPath);
        Assert.AreEqual("file.txt", record.FileName);
    }

    [TestMethod]
    public void MftRecord_FileName_NoPathNoName_ReturnsEmpty()
    {
        var record = new MftRecord(0, 5, 1, null, null);
        Assert.AreEqual(string.Empty, record.FileName);
        Assert.IsNull(record.FullPath);
    }

    [TestMethod]
    public void MftRecord_ToString_ReturnsFullPathOrFileName()
    {
        var withPath = new MftRecord(0, 5, 1, "file.txt", "dir\\file.txt");
        Assert.AreEqual("dir\\file.txt", withPath.ToString());

        var withoutPath = new MftRecord(0, 5, 1, "orphan.txt", null);
        Assert.AreEqual("orphan.txt", withoutPath.ToString());
    }

    [TestMethod]
    public void MftRecord_FileAttributes_ReturnsStoredValue()
    {
        var record = new MftRecord(0, 5, 1, "test.txt", null, FileAttributes.Hidden | FileAttributes.ReadOnly);
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.ReadOnly, record.FileAttributes);
    }
}
