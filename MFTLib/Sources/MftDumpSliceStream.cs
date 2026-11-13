using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     One parsed slice copied out of the native parser: packed <c>MftCompactEntry</c> rows and the UTF-16
///     units their string offsets count from. The arrays are pooled; <see cref="Return" /> gives them back.
/// </summary>
sealed class MftDumpSlice
{
    internal MftDumpSlice(int entryCount, int stringUnits)
    {
        EntryCount = entryCount;
        StringUnits = stringUnits;
        Entries = ArrayPool<byte>.Shared.Rent(entryCount * (int)MFTLibNative.NativeCompactEntrySize);
        Strings = ArrayPool<char>.Shared.Rent(Math.Max(stringUnits, 1));
    }

    internal byte[] Entries { get; }

    internal char[] Strings { get; }

    internal int EntryCount { get; }

    internal int StringUnits { get; }

    internal void Return()
    {
        ArrayPool<byte>.Shared.Return(Entries);
        ArrayPool<char>.Shared.Return(Strings);
    }
}

/// <summary>
///     Runs a streaming dump parse on its own thread and hands its slices to the consuming thread through a
///     bounded queue, so the consumer writes rows while later chunks are still being parsed and a slow consumer
///     holds the parser back instead of growing memory. The native callbacks never throw: a failure in them is
///     captured, stops the parse, and is rethrown by <see cref="Finish" />.
/// </summary>
sealed unsafe class MftDumpSliceStream : IDisposable
{
    const int QueueCapacity = 64;

    readonly MftDumpInput _input;
    readonly IProgress<MftScanProgress>? _progress;
    readonly ParseThreadAllowance? _allowance;
    readonly CancellationToken _cancellationToken;
    readonly BlockingCollection<MftDumpSlice> _queue = new(QueueCapacity);

    GCHandle _self;
    Thread? _parseThread;
    IntPtr _result;
    Exception? _failure;
    MftExtensionDataSize[] _extensionDataSizes = [];
    volatile bool _stop;
    bool _finished;

    internal MftDumpSliceStream(MftDumpInput input, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? allowance, CancellationToken cancellationToken)
    {
        _input = input;
        _progress = progress;
        _allowance = allowance;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The extension data sizes the parse delivered after its last slice; read after <see cref="Finish" />.</summary>
    internal ReadOnlySpan<MftExtensionDataSize> ExtensionDataSizes => _extensionDataSizes;

    /// <summary>Starts the parse thread.</summary>
    internal void Start()
    {
        MFTLibNative.EnsureCompatibleNativeAbi();
        _self = GCHandle.Alloc(this);
        _parseThread = new Thread(ParseOnThread) { IsBackground = true, Name = "MFT dump streaming parse" };
        _parseThread.Start();
    }

    /// <summary>The parsed slices in record order, ending when the parse returns. Return each slice once consumed.</summary>
    internal IEnumerable<MftDumpSlice> Slices() => _queue.GetConsumingEnumerable();

    /// <summary>
    ///     Waits for the parse to return, then throws what stopped it: a callback failure, or the result's own
    ///     cancellation, rejection or failure, with the same exception types a whole-result parse throws.
    /// </summary>
    internal void Finish()
    {
        _parseThread!.Join();
        _finished = true;
        if (_failure is { } failure)
        {
            ExceptionDispatchInfoThrow(failure);
        }

        var result = _result;
        _result = IntPtr.Zero;
        if (result == IntPtr.Zero)
        {
            throw new InvalidOperationException("ParseMftDumpInputStreaming returned null");
        }

        // The constructor frees a failed result and throws its classified exception.
        using var parsed = new MftResult(result, _cancellationToken);
    }

    /// <summary>Stops a parse the consumer abandoned and drains its queue so the parser is never left blocked.</summary>
    internal void Stop()
    {
        if (_parseThread is not { } thread || _finished)
        {
            return;
        }

        _stop = true;
        foreach (var slice in _queue.GetConsumingEnumerable())
        {
            slice.Return();
        }

        thread.Join();
        _finished = true;
    }

    /// <summary>Stops the parse if it still runs and frees the result.</summary>
    public void Dispose()
    {
        Stop();

        if (_result != IntPtr.Zero)
        {
            MFTLibNative.Hooks._freeMftResult(_result);
            _result = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _queue.Dispose();
    }

    static void ExceptionDispatchInfoThrow(Exception failure) =>
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

    void ParseOnThread()
    {
        var control = (MftParseControl*)NativeMemory.AllocZeroed((nuint)sizeof(MftParseControl));
        try
        {
            _allowance?.Attach(&control->ParseThreadAllowance);
            try
            {
                var controlAddress = (IntPtr)control;
                using var cancellation = _cancellationToken.Register(() => RequestCancel(controlAddress));
                using var registration = new NativeProgressRegistration(_progress);
                delegate* unmanaged[Cdecl]<IntPtr, byte*, ulong, ushort*, ulong, int> slice = &OnSlice;
                delegate* unmanaged[Cdecl]<IntPtr, MftExtensionDataSize*, ulong, int> extensions = &OnExtensions;
                var sink = new MftParseSink
                {
                    Slice = (IntPtr)slice, Extensions = (IntPtr)extensions, Context = GCHandle.ToIntPtr(_self)
                };
                _result = MFTLibNative.NativeParseMftDumpInputStreaming(_input.Handle, _input.ChunkRecords,
                    controlAddress, registration.Callback.Function, registration.Callback.Context, (IntPtr)(&sink));
            }
            finally
            {
                _allowance?.Detach();
            }
        }
        catch (Exception exception)
        {
            _failure ??= exception;
        }
        finally
        {
            NativeMemory.Free(control);
            _queue.CompleteAdding();
        }
    }

    static void RequestCancel(IntPtr control)
    {
        Volatile.Write(ref ((MftParseControl*)control)->CancelRequested, 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSlice(IntPtr context, byte* entries, ulong entryCount, ushort* strings, ulong stringUnits)
    {
        var stream = (MftDumpSliceStream)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (stream._stop)
            {
                return 1;
            }

            var slice = new MftDumpSlice(checked((int)entryCount), checked((int)stringUnits));
            new ReadOnlySpan<byte>(entries, checked((int)(entryCount * MFTLibNative.NativeCompactEntrySize)))
                .CopyTo(slice.Entries);
            new ReadOnlySpan<char>(strings, (int)stringUnits).CopyTo(slice.Strings);
            stream._queue.Add(slice);
            return stream._stop ? 1 : 0;
        }
        catch (Exception exception)
        {
            stream._failure ??= exception;
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnExtensions(IntPtr context, MftExtensionDataSize* sizes, ulong count)
    {
        var stream = (MftDumpSliceStream)GCHandle.FromIntPtr(context).Target!;
        try
        {
            stream._extensionDataSizes = new ReadOnlySpan<MftExtensionDataSize>(sizes, checked((int)count)).ToArray();
            return stream._stop ? 1 : 0;
        }
        catch (Exception exception)
        {
            stream._failure ??= exception;
            return 1;
        }
    }
}

/// <summary>The native <c>MftExtensionDataSize</c>: 18 packed bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
readonly struct MftExtensionDataSize
{
    public readonly ulong BaseRecordNumber;
    public readonly long Size;
    public readonly ushort BaseSequenceNumber;
}

/// <summary>The native <c>MftParseSink</c>: two function pointers and their context.</summary>
[StructLayout(LayoutKind.Sequential)]
struct MftParseSink
{
    public IntPtr Slice;
    public IntPtr Extensions;
    public IntPtr Context;
}

/// <summary>Step-one consumer: materializes each streamed slice as an <see cref="MftRecord" /> batch.</summary>
static class MftDumpSliceRecords
{
    /// <summary>
    ///     Starts the parse, yields one record batch per slice while the parse continues, then applies the
    ///     extension data sizes to the rows already written into <paramref name="block" />.
    /// </summary>
    internal static IEnumerable<IReadOnlyList<MftRecord>> Read(MftDumpSliceStream stream, BlockFile block)
    {
        stream.Start();
        try
        {
            foreach (var slice in stream.Slices())
            {
                MftRecord[] batch;
                try
                {
                    batch = Materialize(slice);
                }
                finally
                {
                    slice.Return();
                }

                yield return batch;
            }

            stream.Finish();
            ApplyExtensionDataSizes(new BlockWriter(block), stream.ExtensionDataSizes);
        }
        finally
        {
            stream.Stop();
        }
    }

    internal static void ApplyExtensionDataSizes(BlockWriter writer, ReadOnlySpan<MftExtensionDataSize> sizes)
    {
        foreach (ref readonly var extension in sizes)
        {
            if (extension.BaseRecordNumber <= uint.MaxValue)
            {
                writer.TryApplyScannedDataSize((uint)extension.BaseRecordNumber, extension.BaseSequenceNumber,
                    extension.Size);
            }
        }
    }

    static unsafe MftRecord[] Materialize(MftDumpSlice slice)
    {
        var batch = new MftRecord[slice.EntryCount];
        fixed (byte* table = slice.Entries)
        fixed (char* pool = slice.Strings)
        {
            for (var index = 0; index < batch.Length; index++)
            {
                batch[index] = MftResult.GetCompactEntry(table, (ushort*)pool, (ulong)slice.StringUnits,
                    (ulong)index);
            }
        }

        return batch;
    }
}
