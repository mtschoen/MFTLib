using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

public sealed partial class MftVolume : IDisposable
{
    readonly uint _bufferSizeRecords;
    readonly string _driveLetter;
    readonly SafeFileHandle _volumeHandle;
    bool _disposed;

    MftVolume(SafeFileHandle volumeHandle, string driveLetter, uint bufferSizeRecords)
    {
        _volumeHandle = volumeHandle;
        _driveLetter = driveLetter;
        _bufferSizeRecords = bufferSizeRecords;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _volumeHandle.Dispose();
            _disposed = true;
        }
    }

    internal SafeFileHandle GetVolumeHandleForTest()
    {
        return _volumeHandle;
    }

    public static MftVolume Open(string volumePath, uint bufferSizeRecords = 262144)
    {
        var normalizedPath = MFTUtilities.GetVolumePath(volumePath);
        var handle = FileUtilities._getVolumeHandle(normalizedPath);

        var driveLetter = ExtractDriveLetter(normalizedPath);

        return new MftVolume(handle, driveLetter, bufferSizeRecords);
    }

    public MftRecord[] ReadAllRecords()
    {
        return ReadAllRecords(false, out _);
    }

    public MftRecord[] ReadAllRecords(bool resolvePaths)
    {
        return ReadAllRecords(resolvePaths, out _);
    }

    public MftRecord[] ReadAllRecords(out MftParseTimings timings)
    {
        return ReadAllRecords(false, out timings);
    }

    public MftRecord[] ReadAllRecords(bool resolvePaths, out MftParseTimings timings)
    {
        using var result = StreamRecords(null, resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None);
        return MaterializeWithTimings(result, out timings);
    }

    public IEnumerable<MftRecord[]> ReadRecordBatches(
        bool resolvePaths = false, int batchSize = 4096, IProgress<MftScanProgress>? progress = null)
    {
        using var result = StreamRecords(
            null, resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None, progress);
        foreach (var batch in result.MaterializeBatches(batchSize))
        {
            yield return batch;
        }
    }

    public MftRecord[] FindByName(string name, MatchFlags matchFlags = MatchFlags.ExactMatch)
    {
        return FindByName(name, matchFlags, out _);
    }

    public MftRecord[] FindByName(string name, MatchFlags matchFlags, out MftParseTimings timings)
    {
        using var result = StreamRecords(name, matchFlags);
        return MaterializeWithTimings(result, out timings);
    }

    /// <summary>
    ///     Parses MFT records, optionally reporting progress as the native scan runs.
    /// </summary>
    /// <param name="filter">An optional name filter passed to the native parser.</param>
    /// <param name="matchFlags">Flags controlling how <paramref name="filter" /> is matched and whether paths are resolved.</param>
    /// <param name="progress">
    ///     Receives one <see cref="MftScanProgress" /> sample per native progress
    ///     callback. <see cref="IProgress{T}.Report" /> is invoked synchronously while
    ///     the parse and resolution passes run (serialized across native parse and
    ///     resolve worker threads), not after they complete, so consumers must be
    ///     thread-safe, implementations must be cheap, and must not throw:
    ///     any exception raised while constructing a sample or reporting it is
    ///     swallowed here to preserve the never-throw-across-the-unmanaged-boundary
    ///     guarantee, and that sample is simply dropped.
    /// </param>
    public MftResult StreamRecords(
        string? filter = null,
        MatchFlags matchFlags = MatchFlags.None,
        IProgress<MftScanProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MFTLibNative.EnsureCompatibleNativeAbi();

        MFTLibNative.NativeMftProgressCallback? nativeCallback = progress != null
            ? (phase, recordsScanned, totalRecords, elapsedMs, _) =>
            {
                try
                {
                    var sample = new MftScanProgress(phase, (long)recordsScanned, (long)totalRecords,
                        TimeSpan.FromMilliseconds(elapsedMs));
                    progress.Report(sample);
                }
                catch
                {
                    // Non-throwing adapter: never throw across unmanaged boundary. This
                    // callback runs synchronously on the native parse thread, so a
                    // malformed sample (e.g. NaN elapsedMs) or a throwing consumer must
                    // not abort the parse - the sample is simply dropped.
                }
            }
        : null;

        var resultPtr = MFTLibNative._parseMftRecordsWithProgress(
            _volumeHandle, filter, matchFlags, _bufferSizeRecords, nativeCallback, IntPtr.Zero);
        GC.KeepAlive(nativeCallback);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("ParseMFTRecords returned null");
        }

        return new MftResult(resultPtr, _driveLetter, 0);
    }


    public IEnumerable<string> FindDirectories(string name)
    {
        return FindRecords(name, true);
    }

    public IEnumerable<string> FindFiles(string name)
    {
        return FindRecords(name, false);
    }

    public IEnumerable<string> FindRecords(string name, bool? isDirectory = null)
    {
        using var result = StreamRecords(name, MatchFlags.ExactMatch | MatchFlags.ResolvePaths);

        foreach (var record in result)
        {
            if (isDirectory.HasValue && record.IsDirectory != isDirectory.Value)
            {
                continue;
            }

            yield return record.FullPath ?? record.FileName;
        }
    }

    public static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = 262144)
    {
        if (!MFTLibNative._generateSyntheticMft(filePath, recordCount, bufferSizeRecords))
        {
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
        }
    }

    public static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords,
        uint recordSize)
    {
        if (!MFTLibNative._generateSyntheticMftSized(filePath, recordCount, bufferSizeRecords, recordSize))
        {
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
        }
    }

    public static MftRecord[] ParseMFTFromFile(string filePath, out MftParseTimings timings)
    {
        return ParseMFTFromFile(filePath, null, MatchFlags.None, out timings);
    }

    public static MftRecord[] ParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags,
        out MftParseTimings timings, uint bufferSizeRecords = 262144)
    {
        using var result = StreamMFTFromFile(filePath, filter, matchFlags, bufferSizeRecords);
        return MaterializeWithTimings(result, out timings);
    }

    public static MftResult StreamMFTFromFile(
        string filePath, string? filter = null, MatchFlags matchFlags = MatchFlags.None,
        uint bufferSizeRecords = 262144)
    {
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(filePath, filter, matchFlags, bufferSizeRecords);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("ParseMFTFromFile returned null");
        }

        return new MftResult(resultPtr, string.Empty, 0);
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
        if (normalizedPath.Length != 6)
        {
            return string.Empty;
        }

        if (!normalizedPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (normalizedPath[5] != ':')
        {
            return string.Empty;
        }

        return normalizedPath[4].ToString();
    }
}
