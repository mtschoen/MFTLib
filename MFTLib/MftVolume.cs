using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

public sealed class MftVolume : IDisposable
{
    readonly SafeFileHandle _volumeHandle;
    readonly string _driveLetter;
    readonly uint _bufferSizeRecords;
    bool _disposed;

    MftVolume(SafeFileHandle volumeHandle, string driveLetter, uint bufferSizeRecords)
    {
        _volumeHandle = volumeHandle;
        _driveLetter = driveLetter;
        _bufferSizeRecords = bufferSizeRecords;
    }

    public static MftVolume Open(string volumePath, uint bufferSizeRecords = 262144)
    {
        var normalizedPath = MFTUtilities.GetVolumePath(volumePath);
        var handle = FileUtilities.GetVolumeHandle(normalizedPath);

        var driveLetter = ExtractDriveLetter(normalizedPath);

        return new MftVolume(handle, driveLetter, bufferSizeRecords);
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
        return MaterializeWithTimings(result, out timings);
    }

    public MftRecord[] FindByName(string name, MatchFlags matchFlags = MatchFlags.ExactMatch)
        => FindByName(name, matchFlags, out _);

    public MftRecord[] FindByName(string name, MatchFlags matchFlags, out MftParseTimings timings)
    {
        using var result = StreamRecords(name, matchFlags);
        return MaterializeWithTimings(result, out timings);
    }

    public MftResult StreamRecords(string? filter = null, MatchFlags matchFlags = MatchFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resultPtr = MFTLibNative.ParseMFTRecords(_volumeHandle, filter, matchFlags, _bufferSizeRecords);

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

    public static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = 262144)
    {
        if (!MFTLibNative.GenerateSyntheticMFT(filePath, recordCount, bufferSizeRecords))
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
    }

    public static MftRecord[] ParseMFTFromFile(string filePath, out MftParseTimings timings)
        => ParseMFTFromFile(filePath, null, MatchFlags.None, out timings);

    public static MftRecord[] ParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags, out MftParseTimings timings, uint bufferSizeRecords = 262144)
    {
        var resultPtr = MFTLibNative.ParseMFTFromFile(filePath, filter, matchFlags, bufferSizeRecords);

        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTFromFile returned null");

        using var result = new MftResult(resultPtr, string.Empty, 0);
        return MaterializeWithTimings(result, out timings);
    }

    static MftRecord[] MaterializeWithTimings(MftResult result, out MftParseTimings timings)
    {
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
