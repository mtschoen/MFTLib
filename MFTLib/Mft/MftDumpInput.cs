using Microsoft.Win32.SafeHandles;

namespace MFTLib;

/// <summary>The native dump input, closed exactly once however the owner is released.</summary>
internal sealed class MftDumpInputHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates the empty handle the native open fills in.</summary>
    public MftDumpInputHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        MFTLibNative.CloseMftDumpInput(handle);
        return true;
    }
}

/// <summary>
///     One opened MFT dump file. The file is opened once: the length and record size reported here
///     and every record a parse returns come from that one open file, so replacing the path
///     afterward cannot change what this input reads. A dump is untrusted, so a file whose content
///     fails a check is rejected with <see cref="InvalidDataException" /> and never repaired. An
///     open file protects against a replaced path, not against a writer changing the file in
///     place: a parse that finds the file shorter or longer than it was fails.
/// </summary>
internal sealed class MftDumpInput : IDisposable
{
    internal const string UnsupportedRecordSizeMessage = "Invalid or unsupported MFT record size.";

    readonly MftDumpInputHandle _handle;

    MftDumpInput(MftDumpInputHandle handle, long lengthBytes, uint recordSize)
    {
        _handle = handle;
        LengthBytes = lengthBytes;
        RecordSize = recordSize;
    }

    /// <summary>The length of the opened file in bytes, a whole number of records.</summary>
    public long LengthBytes { get; }

    /// <summary>The record size record zero declares: 1024 or 4096.</summary>
    public uint RecordSize { get; }

    /// <summary>
    ///     The sizing a block is planned from: the dump's own length and record size, never those of
    ///     a live volume.
    /// </summary>
    public NtfsVolumeInformation VolumeInformation => new(LengthBytes, RecordSize);

    /// <summary>Opens a dump and reads its geometry from record zero.</summary>
    /// <param name="filePath">The dump file.</param>
    /// <returns>The opened input, which the caller disposes.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath" /> is null, empty or contains a null character.</exception>
    /// <exception cref="IOException">The file could not be opened or sized; the message carries the platform error code.</exception>
    /// <exception cref="InvalidDataException">
    ///     The file is empty, record zero does not declare a 1024-byte or 4096-byte FILE record, or the
    ///     length is not a whole number of records.
    /// </exception>
    public static MftDumpInput Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (filePath.Contains('\0'))
        {
            // The native open reads the path up to its first null, which would name another file.
            throw new ArgumentException("A dump file path cannot contain a null character.", nameof(filePath));
        }

        MFTLibNative.EnsureCompatibleNativeAbi();

        var handle = MFTLibNative.OpenMftDumpInput(MFTLibNative.NullTerminatedUtf8(filePath), out var info);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw info.InvalidInput != 0
                ? new InvalidDataException(info.ErrorMessage)
                : new IOException(info.ErrorMessage);
        }

        // The native parser reads every power-of-two record size; a dump source accepts only the
        // two sizes NTFS formats.
        if (info.RecordSize is not (1024 or 4096))
        {
            handle.Dispose();
            throw new InvalidDataException(UnsupportedRecordSizeMessage);
        }

        return new MftDumpInput(handle, checked((long)info.LengthBytes), info.RecordSize);
    }

    /// <summary>
    ///     Parses every allocated base record of the opened file and keeps the native result.
    /// </summary>
    /// <param name="options">Progress, thread allowance, cancellation and chunk buffer size; a zero buffer size reads 64 MiB per chunk.</param>
    /// <returns>The native result, which the caller disposes.</returns>
    /// <exception cref="InvalidDataException">An allocated record has an invalid fixup, or the file could not be read to its opened length.</exception>
    /// <exception cref="OperationCanceledException">The token stopped the parse.</exception>
    public MftResult Parse(MftFileScanOptions options = default)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var bufferSizeRecords = options.BufferSizeRecords > 0
            ? options.BufferSizeRecords
            : LiveVolumeSources.HostScanChunkRecords(RecordSize);
        return MftVolume.ParseToResult(
            (control, callback) => MFTLibNative.ParseMftDumpInput(_handle, bufferSizeRecords, control, callback,
                IntPtr.Zero),
            "ParseMftDumpInput", options.Progress, options.ParseThreads, options.CancellationToken);
    }

    /// <summary>
    ///     Parses the opened file, then yields its records in materialized batches of at most
    ///     <paramref name="batchSize" />. The parse starts on the first enumeration.
    /// </summary>
    public IEnumerable<MftRecord[]> ReadRecordBatches(int batchSize, IProgress<MftScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var result = Parse(new MftFileScanOptions(progress, CancellationToken: cancellationToken,
            BufferSizeRecords: 0));
        foreach (var batch in result.MaterializeBatches(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
    }

    /// <summary>Closes the file. Safe to call more than once.</summary>
    public void Dispose()
    {
        _handle.Dispose();
    }
}
