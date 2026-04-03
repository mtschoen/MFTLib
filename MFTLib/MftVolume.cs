using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MFTLib.Interop;

namespace MFTLib;

public sealed class MftVolume : IDisposable
{
    private readonly SafeFileHandle _volumeHandle;
    private readonly string _driveLetter;
    private bool _disposed;

    public uint BufferSizeRecords { get; set; } = 262144;

    private MftVolume(SafeFileHandle volumeHandle, string driveLetter)
    {
        _volumeHandle = volumeHandle;
        _driveLetter = driveLetter;
    }

    public static MftVolume Open(string volumePath)
    {
        var normalizedPath = MFTUtilities.GetVolumePath(volumePath);
        var handle = FileUtilities.GetVolumeHandle(normalizedPath);
        
        // Extract drive letter for path resolution — matches \\.\X: format from GetVolumePath
        var driveLetter = ExtractDriveLetter(normalizedPath);

        return new MftVolume(handle, driveLetter);
    }

    public MftRecord[] ReadAllRecords() => ReadAllRecords(resolvePaths: false, out _);

    public MftRecord[] ReadAllRecords(bool resolvePaths) => ReadAllRecords(resolvePaths, out _);

    public MftRecord[] ReadAllRecords(out MftParseTimings timings)
    {
        return ReadAllRecords(resolvePaths: false, out timings);
    }

    public MftRecord[] ReadAllRecords(bool resolvePaths, out MftParseTimings timings)
    {
        using var result = StreamRecords(null, resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None);
        var sw = Stopwatch.StartNew();
        var records = result.ToArray();
        sw.Stop();
        timings = result.Timings.WithMarshalMs(sw.Elapsed.TotalMilliseconds);
        return records;
    }

    public MftRecord[] FindByName(string name, bool exactMatch = true)
        => FindByName(name, exactMatch, out _);

    public MftRecord[] FindByName(string name, bool exactMatch, out MftParseTimings timings)
        => FindByName(name, exactMatch, resolvePaths: false, out timings);

    public MftRecord[] FindByName(string name, bool exactMatch, bool resolvePaths, out MftParseTimings timings)
    {
        var matchFlags = (exactMatch ? MatchFlags.ExactMatch : MatchFlags.Contains)
            | (resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None);
        using var result = StreamRecords(name, matchFlags);
        var sw = Stopwatch.StartNew();
        var records = result.ToArray();
        sw.Stop();
        timings = result.Timings.WithMarshalMs(sw.Elapsed.TotalMilliseconds);
        return records;
    }

    public MftResult StreamRecords(string? filter = null, MatchFlags matchFlags = MatchFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IntPtr resultPtr = MFTLibNative.ParseMFTRecords(_volumeHandle, filter, matchFlags, BufferSizeRecords);

        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTRecords returned null");

        return new MftResult(resultPtr, _driveLetter, 0);
    }

    public IEnumerable<string> FindDirectories(string name)
    {
        return FindRecords(name, isDirectory: true);
    }

    public IEnumerable<string> FindFiles(string name)
    {
        return FindRecords(name, isDirectory: false);
    }

    public IEnumerable<string> FindRecords(string name, bool? isDirectory = null)
    {
        using var result = StreamRecords(name, MatchFlags.ExactMatch | MatchFlags.ResolvePaths);

        foreach (var record in result)
        {
            if (isDirectory.HasValue && record.IsDirectory != isDirectory.Value)
                continue;

            yield return record.FullPath ?? record.FileName;
        }
    }

    public string ResolvePath(ulong recordNumber)
    {
        var records = ReadAllRecords();
        var lookup = new Dictionary<ulong, MftRecord>();
        foreach (var r in records)
            lookup[r.RecordNumber] = r;
        return ResolvePath(recordNumber, lookup);
    }

    private string ResolvePath(ulong recordNumber, Dictionary<ulong, MftRecord> lookup)
    {
        return MftPathUtilities.ResolvePath(recordNumber, lookup, _driveLetter);
    }

    public static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = 262144)
    {
        if (!MFTLibNative.GenerateSyntheticMFT(filePath, recordCount, bufferSizeRecords))
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
    }

    public static MftRecord[] ParseMFTFromFile(string filePath, out MftParseTimings timings)
        => ParseMFTFromFile(filePath, null, MatchFlags.None, out timings);

    public static MftRecord[] ParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags, out MftParseTimings timings, uint bufferSizeRecords = 262144)
    {
        IntPtr resultPtr = MFTLibNative.ParseMFTFromFile(filePath, filter, matchFlags, bufferSizeRecords);

        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTFromFile returned null");

        using var result = new MftResult(resultPtr, string.Empty, 0);
        var sw = Stopwatch.StartNew();
        var records = result.ToArray();
        sw.Stop();
        timings = result.Timings.WithMarshalMs(sw.Elapsed.TotalMilliseconds);
        return records;
    }

    internal static string ExtractDriveLetter(string normalizedPath)
    {
        if (normalizedPath.Length != 6) return string.Empty;
        if (!normalizedPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return string.Empty;
        if (normalizedPath[5] != ':') return string.Empty;
        return normalizedPath[4].ToString();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _volumeHandle.Dispose();
            _disposed = true;
        }
    }
}
