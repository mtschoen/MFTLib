using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using MFTLib.Interop;

namespace MFTLib.Tests;

/// <summary>
/// Tests for MftVolume, MftResult, and FileUtilities using mocked native calls.
/// These run without admin elevation.
/// </summary>
[TestClass]
public class MockVolumeTests
{
    const int NativeEntrySize = 540;
    const int NativePathEntrySize = 2068;

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        Kernel32.ResetToDefaults();
        MftResult.ParallelThreshold = 500_000;
    }

    static SafeFileHandle FakeHandle() => new(new IntPtr(1), ownsHandle: false);

    static unsafe IntPtr BuildResult(uint usedRecords, bool withPaths = false, string? errorMessage = null)
    {
        var entryBufSize = withPaths ? NativePathEntrySize * (int)usedRecords : NativeEntrySize * (int)usedRecords;
        var entryBuf = Marshal.AllocHGlobal(entryBufSize);
        new Span<byte>((void*)entryBuf, entryBufSize).Clear();

        for (uint i = 0; i < usedRecords; i++)
        {
            if (withPaths)
            {
                var ptr = (byte*)entryBuf + i * NativePathEntrySize;
                Unsafe.WriteUnaligned(ptr, (ulong)i);           // recordNumber
                Unsafe.WriteUnaligned(ptr + 8, (ulong)5);       // parentRecordNumber
                Unsafe.WriteUnaligned(ptr + 16, (ushort)1);     // flags = InUse
                var path = $"dir\\file{i}.txt";
                Unsafe.WriteUnaligned(ptr + 18, (ushort)path.Length); // pathLength
                var pathSpan = new Span<char>(ptr + 20, path.Length);
                path.AsSpan().CopyTo(pathSpan);
            }
            else
            {
                var ptr = (byte*)entryBuf + i * NativeEntrySize;
                Unsafe.WriteUnaligned(ptr, (ulong)i);           // recordNumber
                Unsafe.WriteUnaligned(ptr + 8, (ulong)5);       // parentRecordNumber
                Unsafe.WriteUnaligned(ptr + 16, (ushort)1);     // flags = InUse
                var name = $"file{i}.txt";
                Unsafe.WriteUnaligned(ptr + 18, (ushort)name.Length); // nameLength
                var nameSpan = new Span<char>(ptr + 20, name.Length);
                name.AsSpan().CopyTo(nameSpan);
            }
        }

        var result = new MftParseResult
        {
            TotalRecords = usedRecords,
            UsedRecords = usedRecords,
            Entries = withPaths ? IntPtr.Zero : entryBuf,
            PathEntries = withPaths ? entryBuf : IntPtr.Zero,
            ErrorMessage = errorMessage ?? string.Empty,
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    readonly List<IntPtr> _allocations = [];

    void SetupMocks(uint usedRecords = 3, bool withPaths = false)
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        var resultPtr = BuildResult(usedRecords, withPaths);
        _allocations.Add(resultPtr);
        MFTLibNative.ParseMFTRecords = (_, _, _, _) => resultPtr;
        MFTLibNative.FreeMftResult = ptr =>
        {
            // Read the result to find and free the entry buffer
            var parseResult = Marshal.PtrToStructure<MftParseResult>(ptr);
            var entryBuf = parseResult.Entries != IntPtr.Zero ? parseResult.Entries : parseResult.PathEntries;
            if (entryBuf != IntPtr.Zero) Marshal.FreeHGlobal(entryBuf);
            Marshal.FreeHGlobal(ptr);
        };
    }

    // --- FileUtilities ---

    [TestMethod]
    public void GetVolumeHandle_InvalidVolume_ThrowsIOException()
    {
        // Use the real native GetVolumeHandle - an invalid volume path will fail
        Assert.ThrowsException<IOException>(() => FileUtilities.GetVolumeHandle(@"\\.\ZZZINVALID:"));
    }

    [TestMethod]
    public void GetVolumeHandle_ValidHandle_ReturnsHandle()
    {
        Kernel32.CreateFile = (_, _, _, _, _, _, _) => new SafeFileHandle(new IntPtr(99), ownsHandle: false);
        var handle = FileUtilities.GetVolumeHandle(@"\\.\C:");
        Assert.IsFalse(handle.IsInvalid);
    }

    // --- MftVolume.Open ---

    [TestMethod]
    public void Open_WithMockedHandle_Succeeds()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        using var volume = MftVolume.Open("C");
        Assert.IsNotNull(volume);
    }

    // --- MftVolume.Dispose ---

    [TestMethod]
    public void Dispose_PreventsSubsequentCalls()
    {
        SetupMocks();
        var volume = MftVolume.Open("C");
        volume.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => volume.StreamRecords());
    }

    [TestMethod]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        var volume = MftVolume.Open("C");
        volume.Dispose();
        volume.Dispose(); // Should not throw
    }

    // --- MftVolume.StreamRecords ---

    [TestMethod]
    public void StreamRecords_ReturnsResult()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();
        Assert.IsTrue(result.TotalRecords > 0);
    }

    [TestMethod]
    public void StreamRecords_NullReturn_Throws()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.ParseMFTRecords = (_, _, _, _) => IntPtr.Zero;
        using var volume = MftVolume.Open("C");
        Assert.ThrowsException<InvalidOperationException>(() => volume.StreamRecords());
    }

    // --- MftVolume.ReadAllRecords overloads ---

    [TestMethod]
    public void ReadAllRecords_NoParams_ReturnsRecords()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords();
        Assert.AreEqual(3, records.Length);
    }

    [TestMethod]
    public void ReadAllRecords_WithResolvePaths_ReturnsRecords()
    {
        SetupMocks(withPaths: true);
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(resolvePaths: true);
        Assert.AreEqual(3, records.Length);
        Assert.IsNotNull(records[0].FullPath);
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
    public void ReadAllRecords_WithResolvePathsAndTimings_PopulatesTimings()
    {
        SetupMocks(withPaths: true);
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(resolvePaths: true, out var timings);
        Assert.AreEqual(3, records.Length);
        Assert.IsNotNull(records[0].FullPath);
        Assert.IsTrue(timings.MarshalMs >= 0);
    }

    // --- MftVolume.FindByName overloads ---

    [TestMethod]
    public void FindByName_NoTimings_ReturnsRecords()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        var records = volume.FindByName("test");
        Assert.AreEqual(3, records.Length);
    }

    [TestMethod]
    public void FindByName_WithTimings_ReturnsRecords()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        var records = volume.FindByName("test", MatchFlags.ExactMatch, out var timings);
        Assert.AreEqual(3, records.Length);
        Assert.IsTrue(timings.TotalRecords > 0);
    }

    // --- MftVolume.FindFiles / FindDirectories / FindRecords ---

    [TestMethod]
    public unsafe void FindFiles_ReturnsOnlyFiles()
    {
        // Build entries: one file (flags=1), one directory (flags=3)
        var entryBufSize = NativePathEntrySize * 2;
        var entryBuf = Marshal.AllocHGlobal(entryBufSize);
        new Span<byte>((void*)entryBuf, entryBufSize).Clear();

        // File entry
        var ptr = (byte*)entryBuf;
        Unsafe.WriteUnaligned(ptr, 0UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, (ushort)1); // InUse, not directory
        var path = "test.txt";
        Unsafe.WriteUnaligned(ptr + 18, (ushort)path.Length);
        path.AsSpan().CopyTo(new Span<char>(ptr + 20, path.Length));

        // Directory entry
        ptr = (byte*)entryBuf + NativePathEntrySize;
        Unsafe.WriteUnaligned(ptr, 1UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, (ushort)3); // InUse + Directory
        var dirPath = "somedir";
        Unsafe.WriteUnaligned(ptr + 18, (ushort)dirPath.Length);
        dirPath.AsSpan().CopyTo(new Span<char>(ptr + 20, dirPath.Length));

        var result = new MftParseResult
        {
            TotalRecords = 2,
            UsedRecords = 2,
            Entries = IntPtr.Zero,
            PathEntries = entryBuf,
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.ParseMFTRecords = (_, _, _, _) => resultPtr;
        MFTLibNative.FreeMftResult = p =>
        {
            var r = Marshal.PtrToStructure<MftParseResult>(p);
            if (r.PathEntries != IntPtr.Zero) Marshal.FreeHGlobal(r.PathEntries);
            Marshal.FreeHGlobal(p);
        };

        using var volume = MftVolume.Open("C");
        var files = volume.FindFiles("test.txt").ToList();

        Assert.AreEqual(1, files.Count);
        Assert.IsTrue(files[0].EndsWith("test.txt"));
    }

    [TestMethod]
    public unsafe void FindDirectories_ReturnsOnlyDirectories()
    {
        var entryBufSize = NativePathEntrySize * 2;
        var entryBuf = Marshal.AllocHGlobal(entryBufSize);
        new Span<byte>((void*)entryBuf, entryBufSize).Clear();

        // File entry
        var ptr = (byte*)entryBuf;
        Unsafe.WriteUnaligned(ptr, 0UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, (ushort)1);
        var filePath = "test.txt";
        Unsafe.WriteUnaligned(ptr + 18, (ushort)filePath.Length);
        filePath.AsSpan().CopyTo(new Span<char>(ptr + 20, filePath.Length));

        // Directory entry
        ptr = (byte*)entryBuf + NativePathEntrySize;
        Unsafe.WriteUnaligned(ptr, 1UL);
        Unsafe.WriteUnaligned(ptr + 8, 5UL);
        Unsafe.WriteUnaligned(ptr + 16, (ushort)3);
        var dirPath = "somedir";
        Unsafe.WriteUnaligned(ptr + 18, (ushort)dirPath.Length);
        dirPath.AsSpan().CopyTo(new Span<char>(ptr + 20, dirPath.Length));

        var result = new MftParseResult
        {
            TotalRecords = 2,
            UsedRecords = 2,
            Entries = IntPtr.Zero,
            PathEntries = entryBuf,
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.ParseMFTRecords = (_, _, _, _) => resultPtr;
        MFTLibNative.FreeMftResult = p =>
        {
            var r = Marshal.PtrToStructure<MftParseResult>(p);
            if (r.PathEntries != IntPtr.Zero) Marshal.FreeHGlobal(r.PathEntries);
            Marshal.FreeHGlobal(p);
        };

        using var volume = MftVolume.Open("C");
        var directories = volume.FindDirectories("somedir").ToList();

        Assert.AreEqual(1, directories.Count);
        Assert.IsTrue(directories[0].EndsWith("somedir"));
    }

    [TestMethod]
    public void FindRecords_NullDirectoryFilter_ReturnsBoth()
    {
        SetupMocks(withPaths: true);
        using var volume = MftVolume.Open("C");
        var results = volume.FindRecords("test").ToList();
        Assert.IsTrue(results.Count > 0);
    }

    // --- MftResult properties and enumerator ---

    [TestMethod]
    public void MftResult_TotalRecords_MatchesExpected()
    {
        SetupMocks(usedRecords: 5);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();
        Assert.AreEqual(5UL, result.TotalRecords);
    }

    [TestMethod]
    public void MftResult_UsedRecords_MatchesExpected()
    {
        SetupMocks(usedRecords: 5);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();
        Assert.AreEqual(5UL, result.UsedRecords);
    }

    [TestMethod]
    public void MftResult_NonGenericEnumerator_Works()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

        IEnumerable enumerable = result;
        var count = 0;
        foreach (var item in enumerable)
        {
            Assert.IsInstanceOfType(item, typeof(MftRecord));
            count++;
        }
        Assert.AreEqual(3, count);
    }

    [TestMethod]
    public void MftResult_Enumerate_WithEntries_ReadsRecords()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

        var records = new List<MftRecord>();
        foreach (var record in result)
            records.Add(record.Materialize());

        Assert.AreEqual(3, records.Count);
        Assert.AreEqual("file0.txt", records[0].FileName);
    }

    [TestMethod]
    public void MftResult_Enumerate_WithPathEntries_ReadsRecords()
    {
        SetupMocks(withPaths: true);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

        var records = new List<MftRecord>();
        foreach (var record in result)
            records.Add(record.Materialize());

        Assert.AreEqual(3, records.Count);
        Assert.IsNotNull(records[0].FullPath);
    }

    [TestMethod]
    public void MftResult_Dispose_PreventsEnumeration()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords();
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() =>
        {
            foreach (var _ in result) { }
        });
    }

    [TestMethod]
    public void MftResult_Dispose_PreventsToArray()
    {
        SetupMocks();
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords();
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => result.ToArray());
    }

    [TestMethod]
    public void MftResult_ToArray_ParallelPath_MaterializesRecords()
    {
        MftResult.ParallelThreshold = 2; // Lower threshold to trigger parallel path
        SetupMocks(usedRecords: 5);
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();
        var records = result.ToArray();

        Assert.AreEqual(5, records.Length);
        Assert.AreEqual("file0.txt", records[0].FileName);
        Assert.AreEqual("file4.txt", records[4].FileName);
    }
}
