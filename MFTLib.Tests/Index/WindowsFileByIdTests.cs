using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class WindowsFileByIdTests
{
    [TestCleanup]
    public void Cleanup()
    {
        Kernel32.Hooks.ResetToDefaults();
    }
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Open_RoundTripsARealFileThroughItsFileReference()
    {
        using var temporaryFile = new TempFile("mftlib-openbyid", create: false);
        var path = temporaryFile.Path;
        File.WriteAllText(path, "round trip");
        var (recordNumber, sequenceNumber) = FileReferenceProbe.Read(path);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))!;

        using var stream = WindowsFileById.Open(volumeRoot, recordNumber, sequenceNumber, FileAccess.Read);
        using var reader = new StreamReader(stream);

        Assert.AreEqual("round trip", reader.ReadToEnd());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Open_WithWriteAccess_OpensAndAcceptsAWrite()
    {
        using var temporaryFile = new TempFile("mftlib-openbyid", create: false);
        var path = temporaryFile.Path;
        File.WriteAllText(path, "before");
        var (recordNumber, sequenceNumber) = FileReferenceProbe.Read(path);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))!;

        using (var stream = WindowsFileById.Open(volumeRoot, recordNumber, sequenceNumber, FileAccess.Write))
        {
            stream.SetLength(0);
            using var writer = new StreamWriter(stream);
            writer.Write("after");
        }

        Assert.AreEqual("after", File.ReadAllText(path));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Open_WithReadWriteAccess_ReadsAndWritesThroughTheSameHandle()
    {
        using var temporaryFile = new TempFile("mftlib-openbyid", create: false);
        var path = temporaryFile.Path;
        File.WriteAllText(path, "before");
        var (recordNumber, sequenceNumber) = FileReferenceProbe.Read(path);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))!;

        using var stream = WindowsFileById.Open(volumeRoot, recordNumber, sequenceNumber, FileAccess.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.AreEqual("before", reader.ReadToEnd());

        stream.SetLength(0);
        stream.Seek(0, SeekOrigin.Begin);
        using var writer = new StreamWriter(stream);
        writer.Write("after");
        writer.Flush();

        Assert.AreEqual(5, stream.Length);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Open_InvalidVolumeHintPath_ThrowsIOExceptionNamingThePath()
    {
        using var missingDirectory = new TempDirectory("mftlib-missing-hint", create: false);
        var missingPath = Path.Combine(missingDirectory.Path, "no-such-directory");

        var failedHandle = new SafeFileHandle(new IntPtr(-1), false);
        Kernel32.Hooks._createFile = (_, _, _, _, _, _, _) =>
        {
            Marshal.SetLastPInvokeError(3);
            return failedHandle;
        };

        var failure = Assert.ThrowsExactly<IOException>(
            () => WindowsFileById.Open(missingPath, recordNumber: 5, sequenceNumber: 1, FileAccess.Read));

        Assert.Contains(missingPath, failure.Message);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Open_UnrecognizedFileAccessValue_ThrowsArgumentOutOfRangeException()
    {
        using var temporaryFile = new TempFile("mftlib-openbyid", create: false);
        var path = temporaryFile.Path;
        File.WriteAllText(path, "x");
        var (recordNumber, sequenceNumber) = FileReferenceProbe.Read(path);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))!;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => WindowsFileById.Open(volumeRoot, recordNumber, sequenceNumber, default));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Open_ThrowsIOExceptionNamingTheRecordWhenTheSequenceNumberIsWrong()
    {
        using var temporaryFile = new TempFile("mftlib-openbyid", create: false);
        var path = temporaryFile.Path;
        File.WriteAllText(path, "x");
        var (recordNumber, sequenceNumber) = FileReferenceProbe.Read(path);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))!;

        var failure = Assert.ThrowsExactly<IOException>(
            () => WindowsFileById.Open(volumeRoot, recordNumber, (ushort)(sequenceNumber + 1), FileAccess.Read));

        Assert.Contains(recordNumber.ToString(CultureInfo.InvariantCulture), failure.Message);
    }
}

/// <summary>
///     Reads the NTFS file reference (record number plus sequence number) an open handle names,
///     the same identity <see cref="WindowsFileById.Open" /> is given to reopen the file by id
///     instead of by path. Test-only: production code never needs to read this back, only to
///     pass it through from the index's own columns.
/// </summary>
[SupportedOSPlatform("windows")]
static class FileReferenceProbe
{
    const uint FileReadAttributes = 0x80;
    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;

    public static (uint RecordNumber, ushort SequenceNumber) Read(string path)
    {
        using var handle = CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, 0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"Could not open '{path}' to read its file reference.",
                Marshal.GetHRForLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException($"Could not read file information for '{path}'.",
                Marshal.GetHRForLastWin32Error());
        }

        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return ((uint)(fileIndex & 0xFFFFFFFF), (ushort)(fileIndex >> 48));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    // Mirrors the Win32 BY_HANDLE_FILE_INFORMATION layout. Pack = 4 and flat uint pairs for
    // each FILETIME member keep every field naturally aligned; declaring a FILETIME member as
    // a single 8-byte field instead would pull in 8-byte alignment and insert four bytes of
    // padding before it, shifting every field after and yielding a garbage file index.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
