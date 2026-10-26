using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.NativeInterop;
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
readonly record struct MftFileScanOptions(
    IProgress<MftScanProgress>? Progress = null,
    ParseThreadAllowance? ParseThreads = null,
    uint BufferSizeRecords = MftVolume.DefaultBufferSizeRecords,
    CancellationToken CancellationToken = default);

/// <summary>
///     A raw read handle on one NTFS volume. Opening needs the Administrator role because it
///     opens the volume device itself. Dispose releases the handle. The scan members here parse
///     the MFT natively; USN journal operations are provided by <see cref="Journal" />.
/// </summary>
sealed class MftVolume : IDisposable
{
    /// <summary>The records the native parser reads per chunk when the caller names no buffer size: 262144.</summary>
    internal const uint DefaultBufferSizeRecords = 262144;

    readonly uint _bufferSizeRecords;
    readonly SafeFileHandle _volumeHandle;
    bool _disposed;

    internal UsnJournalVolume Journal { get; }

    MftVolume(SafeFileHandle volumeHandle, string volumePath, uint bufferSizeRecords)
    {
        _volumeHandle = volumeHandle;
        _bufferSizeRecords = bufferSizeRecords;
        Journal = new UsnJournalVolume(volumeHandle, volumePath);
    }

    /// <summary>Closes the volume handle. Safe to call more than once.</summary>
    internal void Dispose()
    {
        if (!_disposed)
        {
            _volumeHandle.Dispose();
            _disposed = true;
        }
    }

    void IDisposable.Dispose() => Dispose();

    /// <summary>Opens a volume for reading.</summary>
    /// <param name="drive">The drive to open.</param>
    /// <param name="bufferSizeRecords">Records the native parser reads per chunk; also the unit at which cancellation is observed.</param>
    /// <returns>The open volume, which the caller disposes.</returns>
    /// <exception cref="ArgumentException"><paramref name="drive" /> is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferSizeRecords" /> is zero.</exception>
    /// <exception cref="IOException">The volume could not be opened, for example without elevation.</exception>
    internal static MftVolume Open(DriveKey drive, uint bufferSizeRecords = DefaultBufferSizeRecords)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bufferSizeRecords);
        var volumePath = MFTUtilities.GetVolumePath(drive);
        var handle = FileUtilities.Hooks._getVolumeHandle(volumePath);

        return new MftVolume(handle, volumePath, bufferSizeRecords);
    }

    /// <summary>
    ///     Parses every allocated base record of the volume, then yields them in batches of at most
    ///     <paramref name="batchSize" />. The records are materialized; the native result is freed when the
    ///     enumeration ends or is disposed.
    /// </summary>
    /// <param name="batchSize">The maximum number of records per batch.</param>
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
    ///     throws <see cref="OperationCanceledException" />. The token is also checked before each batch is yielded.
    /// </param>
    /// <param name="options">
    ///     Whether the parse also returns the base records NTFS has freed, with <see cref="MftRecord.InUse" /> false;
    ///     null returns allocated records only.
    /// </param>
    /// <param name="recordsOmitted">
    ///     Told, once the parse ends and before the first batch, how many allocated records the scan passed over
    ///     because their fixup was invalid.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="parseThreads" /> is attached to another parse that is still running. The
    ///     parse starts on the first enumeration, so that is when this is thrown.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize" /> is not positive.</exception>
    internal IEnumerable<MftRecord[]> ReadRecordBatches(int batchSize, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? parseThreads, CancellationToken cancellationToken, MftScanOptions? options = null,
        Action<long>? recordsOmitted = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return ParseRecordBatches(batchSize, progress, parseThreads, options, recordsOmitted, cancellationToken);
    }

    IEnumerable<MftRecord[]> ParseRecordBatches(int batchSize, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? parseThreads, MftScanOptions? options, Action<long>? recordsOmitted,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MFTLibNative.EnsureCompatibleNativeAbi();

        var includeFreed = options?.IncludeFreed ?? false;
        using var result = ParseToResult(
            (control, callback) => MFTLibNative.Hooks._parseMftRecordsWithProgress(
                _volumeHandle, includeFreed, _bufferSizeRecords, control, callback),
            "ParseMFTRecords", progress, parseThreads, cancellationToken);
        recordsOmitted?.Invoke(checked((long)result.InvalidFixupRecordCount));
        foreach (var batch in result.ReadBatches(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
    }

    // The one place a streaming parse is assembled, for a volume or a dump alike: the
    // progress adapter, the control block and its allowance and cancellation hooks, the null
    // check and the result wrapper. Only the native call differs.
    internal static MftResult ParseToResult(
        Func<IntPtr, NativeProgressCallback, IntPtr> nativeParse, string nativeName,
        IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)
    {
        IntPtr resultPtr;
        using (var registration = new NativeProgressRegistration(progress))
        {
            resultPtr = ParseWithControl(nativeParse, registration.Callback, parseThreads, cancellationToken);
        }

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
        Func<IntPtr, NativeProgressCallback, IntPtr> nativeParse, NativeProgressCallback progress,
        ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)
    {
        var control = (MftParseControl*)NativeMemory.AllocZeroed((nuint)sizeof(MftParseControl));
        try
        {
            parseThreads?.Attach(&control->ParseThreadAllowance);
            try
            {
                var controlAddress = (IntPtr)control;
                using var registration = cancellationToken.Register(() => RequestCancel(controlAddress));
                return nativeParse(controlAddress, progress);
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

    const uint DefaultSyntheticRecordSize = 1024;

    internal static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = DefaultBufferSizeRecords)
    {
        GenerateSyntheticMFT(filePath, recordCount, bufferSizeRecords, DefaultSyntheticRecordSize);
    }

    internal static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords,
        uint recordSize)
    {
        if (!MFTLibNative.GenerateSyntheticMftSized(filePath, recordCount, bufferSizeRecords, recordSize))
        {
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
        }
    }
}
