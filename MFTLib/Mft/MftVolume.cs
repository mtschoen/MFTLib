using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

/// <summary>
///     A raw read handle on one NTFS volume. Opening needs the Administrator role because it
///     opens the volume device itself. Dispose releases the handle. The scan members here parse
///     the MFT natively; the USN journal members live in the same type.
/// </summary>
public sealed partial class MftVolume : IDisposable
{
    readonly uint _bufferSizeRecords;
    readonly string _driveLetter;
    readonly string _volumePath;
    readonly SafeFileHandle _volumeHandle;
    bool _disposed;

    MftVolume(SafeFileHandle volumeHandle, string volumePath, uint bufferSizeRecords)
    {
        _volumeHandle = volumeHandle;
        _volumePath = volumePath;
        _driveLetter = ExtractDriveLetter(volumePath);
        _bufferSizeRecords = bufferSizeRecords;
    }

    /// <summary>Closes the volume handle. Safe to call more than once.</summary>
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

    /// <summary>Opens a volume for reading.</summary>
    /// <param name="volumePath">
    ///     A drive letter (<c>C</c>, <c>C:</c> or <c>C:\</c>), a raw device path (<c>\\.\C:</c>) or a
    ///     volume GUID path. A GUID path leaves record paths without a drive prefix.
    /// </param>
    /// <param name="bufferSizeRecords">Records the native parser reads per chunk; also the unit at which cancellation is observed.</param>
    /// <returns>The open volume, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="volumePath" /> is null or empty.</exception>
    /// <exception cref="ArgumentException"><paramref name="volumePath" /> is in none of the recognized formats.</exception>
    /// <exception cref="IOException">The volume could not be opened, for example without elevation.</exception>
    public static MftVolume Open(string volumePath, uint bufferSizeRecords = 262144)
    {
        var normalizedPath = MFTUtilities.GetVolumePath(volumePath);
        var handle = FileUtilities._getVolumeHandle(normalizedPath);

        return new MftVolume(handle, normalizedPath, bufferSizeRecords);
    }

    /// <summary>Parses every record, with names but without resolved paths.</summary>
    /// <returns>All records as materialized values that outlive this volume.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    public MftRecord[] ReadAllRecords()
    {
        return ReadAllRecords(false, out _);
    }

    /// <summary>Parses every record, optionally resolving full paths.</summary>
    /// <param name="resolvePaths">True to resolve <see cref="MftRecord.FullPath" />, which adds a resolution pass.</param>
    /// <returns>All records as materialized values that outlive this volume.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    public MftRecord[] ReadAllRecords(bool resolvePaths)
    {
        return ReadAllRecords(resolvePaths, out _);
    }

    /// <summary>Parses every record without resolving paths and reports how long each phase took.</summary>
    /// <param name="timings">Phase timings, including the time spent copying records into managed memory.</param>
    /// <returns>All records as materialized values that outlive this volume.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    public MftRecord[] ReadAllRecords(out MftParseTimings timings)
    {
        return ReadAllRecords(false, out timings);
    }

    /// <summary>Parses every record, optionally resolving paths, and reports how long each phase took.</summary>
    /// <param name="resolvePaths">True to resolve <see cref="MftRecord.FullPath" />, which adds a resolution pass.</param>
    /// <param name="timings">Phase timings, including the time spent copying records into managed memory.</param>
    /// <returns>All records as materialized values that outlive this volume.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    public MftRecord[] ReadAllRecords(bool resolvePaths, out MftParseTimings timings)
    {
        using var result = StreamRecords(
            null, resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None, null, null, CancellationToken.None);
        return MaterializeWithTimings(result, out timings);
    }

    /// <summary>
    ///     Parses every record, then yields them in batches of at most <paramref name="batchSize" />.
    ///     <paramref name="parseThreads" /> and <paramref name="cancellationToken" /> apply as in
    ///     <see cref="StreamRecords" />; the token is also checked before each batch is yielded.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="parseThreads" /> is attached to another parse that is still running. The
    ///     parse starts on the first enumeration, so that is when this is thrown.
    /// </exception>
    internal IEnumerable<MftRecord[]> ReadRecordBatches(bool resolvePaths, int batchSize,
        IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)
    {
        using var result = StreamRecords(
            null, resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None, progress, parseThreads,
            cancellationToken);
        foreach (var batch in result.MaterializeBatches(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
    }

    /// <summary>Parses the MFT and keeps only records whose name matches.</summary>
    /// <param name="name">The name to look for, matched as <paramref name="matchFlags" /> directs.</param>
    /// <param name="matchFlags">Exact or contains matching, plus optional path resolution; defaults to an exact match.</param>
    /// <returns>The matching records as materialized values that outlive this volume.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    public MftRecord[] FindByName(string name, MatchFlags matchFlags = MatchFlags.ExactMatch)
    {
        return FindByName(name, matchFlags, out _);
    }

    /// <summary>Parses the MFT, keeps only records whose name matches, and reports how long each phase took.</summary>
    /// <param name="name">The name to look for, matched as <paramref name="matchFlags" /> directs.</param>
    /// <param name="matchFlags">Exact or contains matching, plus optional path resolution.</param>
    /// <param name="timings">Phase timings, including the time spent copying records into managed memory.</param>
    /// <returns>The matching records as materialized values that outlive this volume.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    public MftRecord[] FindByName(string name, MatchFlags matchFlags, out MftParseTimings timings)
    {
        using var result = StreamRecords(name, matchFlags, null, null, CancellationToken.None);
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
    /// <param name="parseThreads">
    ///     The thread count the parse reads at every chunk and before path resolution; null uses
    ///     every processor. The allowance attaches to this parse until it returns, and one allowance
    ///     serves one running parse at a time.
    /// </param>
    /// <param name="cancellationToken">
    ///     Stops the native parse between chunks, between 4096-record sub-slices, and between
    ///     path-resolution slices; a stopped parse throws <see cref="OperationCanceledException" />.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="parseThreads" /> is attached to another parse that is still running.
    /// </exception>
    public MftResult StreamRecords(string? filter, MatchFlags matchFlags, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MFTLibNative.EnsureCompatibleNativeAbi();

        var nativeCallback = CreateNativeProgressCallback(progress);
        var resultPtr = ParseWithControl(filter, matchFlags, nativeCallback, parseThreads, cancellationToken);
        GC.KeepAlive(nativeCallback);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("ParseMFTRecords returned null");
        }

        return new MftResult(resultPtr, _driveLetter, cancellationToken);
    }

    // Runs the native parse against a control block that stays at one address for the whole call:
    // the allowance writes through to it and the token registration sets its cancellation flag,
    // and both are released before it is freed.
    unsafe IntPtr ParseWithControl(string? filter, MatchFlags matchFlags,
        MFTLibNative.NativeMftProgressCallback? nativeCallback, ParseThreadAllowance? parseThreads,
        CancellationToken cancellationToken)
    {
        var control = (MftParseControl*)NativeMemory.AllocZeroed((nuint)sizeof(MftParseControl));
        try
        {
            parseThreads?.Attach(&control->ParseThreadAllowance);
            try
            {
                var controlAddress = (IntPtr)control;
                using var registration = cancellationToken.Register(() => RequestCancel(controlAddress));
                return MFTLibNative._parseMftRecordsWithProgress(
                    _volumeHandle, filter, matchFlags, _bufferSizeRecords, (IntPtr)control, nativeCallback);
            }
            finally
            {
                parseThreads?.Detach();
            }
        }
        finally
        {
            NativeMemory.Free(control);
        }
    }

    static unsafe void RequestCancel(IntPtr control)
    {
        Volatile.Write(ref ((MftParseControl*)control)->CancelRequested, 1);
    }

    static MFTLibNative.NativeMftProgressCallback? CreateNativeProgressCallback(IProgress<MftScanProgress>? progress)
    {
        return progress != null
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
    }

    const uint DefaultSyntheticRecordSize = 1024;

    internal static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = 262144)
    {
        GenerateSyntheticMFT(filePath, recordCount, bufferSizeRecords, DefaultSyntheticRecordSize);
    }

    internal static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords,
        uint recordSize)
    {
        if (!MFTLibNative._generateSyntheticMftSized(filePath, recordCount, bufferSizeRecords, recordSize))
        {
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
        }
    }

    /// <summary>
    ///     Writes the deterministic size and modified-time fixture: twelve 1024-byte records
    ///     with hand-authored sizes, timestamps, and attribute shapes, covering resident and
    ///     non-resident data, directories, a record whose data attribute lives in an extension
    ///     record, and a non-resident record whose first data attribute has a nonzero lowest
    ///     virtual cluster number. Test support, not a production entry point.
    /// </summary>
    internal static void GenerateFixtureMFT(string filePath)
    {
        if (!MFTLibNative._generateFixtureMft(filePath))
        {
            throw new InvalidOperationException($"GenerateFixtureMFT failed for {filePath}");
        }
    }

    /// <summary>Parses a saved MFT image without resolving paths and without needing a volume or elevation.</summary>
    /// <param name="filePath">The MFT file to parse.</param>
    /// <param name="timings">Phase timings, including the time spent copying records into managed memory.</param>
    /// <returns>All records as materialized values.</returns>
    /// <exception cref="InvalidOperationException">The native parser rejected the file.</exception>
    public static MftRecord[] ParseMFTFromFile(string filePath, out MftParseTimings timings)
    {
        return ParseMFTFromFile(filePath, null, MatchFlags.None, out timings);
    }

    /// <summary>Parses a saved MFT image with an optional name filter, without needing a volume or elevation.</summary>
    /// <param name="filePath">The MFT file to parse.</param>
    /// <param name="filter">A name to keep, or null for every record.</param>
    /// <param name="matchFlags">How <paramref name="filter" /> is matched and whether paths are resolved.</param>
    /// <param name="timings">Phase timings, including the time spent copying records into managed memory.</param>
    /// <param name="bufferSizeRecords">Records the native parser reads per chunk.</param>
    /// <returns>The matching records as materialized values.</returns>
    /// <exception cref="InvalidOperationException">The native parser rejected the file.</exception>
    public static MftRecord[] ParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags,
        out MftParseTimings timings, uint bufferSizeRecords = 262144)
    {
        using var result = StreamMFTFromFile(filePath, filter, matchFlags, bufferSizeRecords);
        return MaterializeWithTimings(result, out timings);
    }

    /// <summary>Parses a saved MFT image and keeps the native result, so records can be enumerated without copying them.</summary>
    /// <param name="filePath">The MFT file to parse.</param>
    /// <param name="filter">A name to keep, or null for every record.</param>
    /// <param name="matchFlags">How <paramref name="filter" /> is matched and whether paths are resolved.</param>
    /// <param name="bufferSizeRecords">Records the native parser reads per chunk.</param>
    /// <returns>The native result, which the caller disposes.</returns>
    /// <exception cref="InvalidOperationException">The native parser rejected the file.</exception>
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

        return new MftResult(resultPtr, string.Empty);
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
