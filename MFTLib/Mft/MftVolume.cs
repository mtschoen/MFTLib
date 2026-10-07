using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

/// <summary>
///     Optional execution controls for <see cref="MftVolume.StreamMftFromFile" />: progress reporting,
///     thread allowance, chunk buffer size, and cancellation.
/// </summary>
/// <param name="Progress">Receives one sample per native progress callback.</param>
/// <param name="ParseThreads">The thread count the parse reads at every chunk; null uses every processor.</param>
/// <param name="BufferSizeRecords">Records the native parser reads per chunk; defaults to <see cref="MftVolume.DefaultBufferSizeRecords" />.</param>
/// <param name="CancellationToken">Stops the native parse; a stopped parse throws <see cref="OperationCanceledException" />.</param>
internal readonly record struct MftFileScanOptions(
    IProgress<MftScanProgress>? Progress = null,
    ParseThreadAllowance? ParseThreads = null,
    uint BufferSizeRecords = MftVolume.DefaultBufferSizeRecords,
    CancellationToken CancellationToken = default);

/// <summary>
///     A raw read handle on one NTFS volume. Opening needs the Administrator role because it
///     opens the volume device itself. Dispose releases the handle. The scan members here parse
///     the MFT natively; the USN journal members live in the same type.
/// </summary>
internal sealed partial class MftVolume : IDisposable
{
    /// <summary>The records the native parser reads per chunk when the caller names no buffer size: 262144.</summary>
    public const uint DefaultBufferSizeRecords = 262144;

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
    ///     volume GUID path (<c>\\?\Volume{guid}</c>, with or without a trailing backslash). A GUID path
    ///     leaves record paths without a drive prefix.
    /// </param>
    /// <param name="bufferSizeRecords">Records the native parser reads per chunk; also the unit at which cancellation is observed.</param>
    /// <returns>The open volume, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="volumePath" /> is null or empty.</exception>
    /// <exception cref="ArgumentException"><paramref name="volumePath" /> is in none of the recognized formats.</exception>
    /// <exception cref="IOException">The volume could not be opened, for example without elevation.</exception>
    public static MftVolume Open(string volumePath, uint bufferSizeRecords = DefaultBufferSizeRecords)
    {
        var normalizedPath = MFTUtilities.GetVolumePath(volumePath);
        var handle = FileUtilities._getVolumeHandle(normalizedPath);

        return new MftVolume(handle, normalizedPath, bufferSizeRecords);
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
    /// <remarks>
    ///     <see cref="MftBatchReadOptions.UnreadableRecords" /> is told, once the parse ends and before the first
    ///     batch, how many allocated records the scan passed over because their fixup was invalid.
    /// </remarks>
    internal IEnumerable<MftRecord[]> ReadRecordBatches(bool resolvePaths, int batchSize,
        IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken,
        MftBatchReadOptions options = default)
    {
        var flags = (resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None)
                    | (options.IncludeFreed ? MatchFlags.IncludeFreed : MatchFlags.None);
        using var result = StreamRecords(null, flags, progress, parseThreads, cancellationToken);
        options.UnreadableRecords?.Invoke(result.InvalidFixupRecordCount);
        foreach (var batch in result.MaterializeBatches(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
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
    /// <exception cref="ArgumentException">
    ///     <paramref name="filter" /> is set and <paramref name="matchFlags" /> has neither
    ///     <see cref="MatchFlags.ExactMatch" /> nor <see cref="MatchFlags.Contains" />.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="parseThreads" /> is attached to another parse that is still running.
    /// </exception>
    public MftResult StreamRecords(string? filter, MatchFlags matchFlags, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateFilter(filter, matchFlags);
        MFTLibNative.EnsureCompatibleNativeAbi();

        return ParseToResult(
            (control, callback) => MFTLibNative._parseMftRecordsWithProgress(
                _volumeHandle, filter, matchFlags, _bufferSizeRecords, control, callback),
            "ParseMFTRecords", _driveLetter, progress, parseThreads, cancellationToken);
    }

    // A filter is matched exactly or by substring; with neither flag the native parser would
    // silently match nothing, so the mistake is reported before any native call.
    static void ValidateFilter(string? filter, MatchFlags matchFlags)
    {
        if (filter != null && (matchFlags & (MatchFlags.ExactMatch | MatchFlags.Contains)) == 0)
        {
            throw new ArgumentException(
                $"A filter needs {nameof(MatchFlags.ExactMatch)} or {nameof(MatchFlags.Contains)} in matchFlags.",
                nameof(matchFlags));
        }
    }

    // The one place a streaming parse is assembled, for a volume or a saved file alike: the
    // progress adapter, the control block and its allowance and cancellation hooks, the null
    // check and the result wrapper. Only the native call differs.
    internal static MftResult ParseToResult(
        Func<IntPtr, MFTLibNative.NativeMftProgressCallback?, IntPtr> nativeParse, string nativeName,
        string driveLetter, IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads,
        CancellationToken cancellationToken)
    {
        var nativeCallback = CreateNativeProgressCallback(progress);
        var resultPtr = ParseWithControl(nativeParse, nativeCallback, parseThreads, cancellationToken);
        GC.KeepAlive(nativeCallback);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{nativeName} returned null");
        }

        return new MftResult(resultPtr, driveLetter, cancellationToken);
    }

    // Runs the native parse against a control block that stays at one address for the whole call:
    // the allowance writes through to it and the token registration sets its cancellation flag,
    // and both are released before it is freed.
    static unsafe IntPtr ParseWithControl(
        Func<IntPtr, MFTLibNative.NativeMftProgressCallback?, IntPtr> nativeParse,
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
                return nativeParse(controlAddress, nativeCallback);
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

    internal static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = DefaultBufferSizeRecords)
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

    /// <summary>
    ///     Parses a saved MFT image and keeps the native result, so records can be enumerated without
    ///     copying them. Needs no volume and no elevation. <paramref name="options" /> controls progress
    ///     reporting, thread allowance, cancellation, and chunk buffer size.
    /// </summary>
    /// <param name="filePath">The MFT file to parse.</param>
    /// <param name="filter">A name to keep, or null for every record.</param>
    /// <param name="matchFlags">How <paramref name="filter" /> is matched and whether paths are resolved.</param>
    /// <param name="options">Progress, thread allowance, cancellation, and chunk buffer size controls.</param>
    /// <returns>The native result, which the caller disposes.</returns>
    /// <exception cref="ArgumentException"><paramref name="filter" /> is set and <paramref name="matchFlags" /> has neither <see cref="MatchFlags.ExactMatch" /> nor <see cref="MatchFlags.Contains" />.</exception>
    /// <exception cref="InvalidDataException">
    ///     The file's content was rejected: it is empty, its record size is unsupported, its length is not a whole
    ///     number of records, an allocated record has an invalid fixup, or it could not be read completely.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     The file could not be opened, or <see cref="MftFileScanOptions.ParseThreads" /> is attached to another parse that is still running.
    /// </exception>
    public static MftResult StreamMftFromFile(string filePath, string? filter = null,
        MatchFlags matchFlags = MatchFlags.None, MftFileScanOptions options = default)
    {
        ValidateFilter(filter, matchFlags);
        MFTLibNative.EnsureCompatibleNativeAbi();

        var bufferSize = options.BufferSizeRecords > 0 ? options.BufferSizeRecords : DefaultBufferSizeRecords;
        return ParseToResult(
            (control, callback) => MFTLibNative._parseMftFromFile(
                filePath, filter, matchFlags, bufferSize, control, callback),
            "ParseMFTFromFile", string.Empty, options.Progress, options.ParseThreads, options.CancellationToken);
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
