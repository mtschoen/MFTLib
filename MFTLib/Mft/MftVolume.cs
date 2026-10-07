using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

/// <summary>
///     Optional execution controls for <see cref="MftDumpInput.Parse" />: progress reporting, thread
///     allowance, chunk buffer size, and cancellation.
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
    readonly string _volumePath;
    readonly SafeFileHandle _volumeHandle;
    bool _disposed;

    MftVolume(SafeFileHandle volumeHandle, string volumePath, uint bufferSizeRecords)
    {
        _volumeHandle = volumeHandle;
        _volumePath = volumePath;
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
    ///     volume GUID path (<c>\\?\Volume{guid}</c>, with or without a trailing backslash).
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
    internal IEnumerable<MftRecord[]> ReadRecordBatches(int batchSize, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? parseThreads, CancellationToken cancellationToken, MftBatchReadOptions options = default)
    {
        using var result = StreamRecords(options.IncludeFreed, progress, parseThreads, cancellationToken);
        options.UnreadableRecords?.Invoke(result.InvalidFixupRecordCount);
        foreach (var batch in result.MaterializeBatches(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
    }

    /// <summary>
    ///     Parses every allocated base record of the volume, optionally reporting progress as the
    ///     native scan runs.
    /// </summary>
    /// <param name="includeFreed">Also returns the base records NTFS has freed, with <see cref="MftRecord.InUse" /> false.</param>
    /// <param name="progress">
    ///     Receives one <see cref="MftScanProgress" /> sample per native progress callback.
    ///     <see cref="IProgress{T}.Report" /> is invoked synchronously on the parse thread after each
    ///     chunk, not after the parse completes, so implementations must be cheap and must not throw:
    ///     any exception raised while constructing a sample or reporting it is swallowed here to
    ///     preserve the never-throw-across-the-unmanaged-boundary guarantee, and that sample is
    ///     simply dropped.
    /// </param>
    /// <param name="parseThreads">
    ///     The thread count the parse reads at every chunk; null uses every processor. The allowance
    ///     attaches to this parse until it returns, and one allowance serves one running parse at a time.
    /// </param>
    /// <param name="cancellationToken">
    ///     Stops the native parse between chunks and between 4096-record sub-slices; a stopped parse
    ///     throws <see cref="OperationCanceledException" />.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="parseThreads" /> is attached to another parse that is still running.
    /// </exception>
    public MftResult StreamRecords(bool includeFreed, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MFTLibNative.EnsureCompatibleNativeAbi();

        return ParseToResult(
            (control, callback) => MFTLibNative._parseMftRecordsWithProgress(
                _volumeHandle, includeFreed, _bufferSizeRecords, control, callback),
            "ParseMFTRecords", progress, parseThreads, cancellationToken);
    }

    // The one place a streaming parse is assembled, for a volume or a dump alike: the
    // progress adapter, the control block and its allowance and cancellation hooks, the null
    // check and the result wrapper. Only the native call differs.
    internal static MftResult ParseToResult(
        Func<IntPtr, MFTLibNative.NativeMftProgressCallback?, IntPtr> nativeParse, string nativeName,
        IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)
    {
        var nativeCallback = CreateNativeProgressCallback(progress);
        var resultPtr = ParseWithControl(nativeParse, nativeCallback, parseThreads, cancellationToken);
        GC.KeepAlive(nativeCallback);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{nativeName} returned null");
        }

        return new MftResult(resultPtr, cancellationToken);
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
            ? (recordsScanned, totalRecords, elapsedMs, _) =>
            {
                try
                {
                    var sample = new MftScanProgress((long)recordsScanned, (long)totalRecords,
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
}
