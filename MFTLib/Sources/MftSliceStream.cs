using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     One parsed slice copied out of the native parser: packed <c>MftCompactEntry</c> rows and the UTF-16
///     units their string offsets count from. The arrays are pooled; <see cref="Return" /> gives them back.
/// </summary>
sealed class MftParsedSlice
{
    internal MftParsedSlice(int entryCount, int stringUnits)
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
///     Runs a streaming native parse (of a dump or a live volume) on its own thread and hands its slices to the consuming thread through a
///     bounded queue, so the consumer writes rows while later chunks are still being parsed and a slow consumer
///     holds the parser back instead of growing memory. The native callbacks never throw: a failure in them is
///     captured, stops the parse, and is rethrown by <see cref="Finish" />.
/// </summary>
sealed unsafe class MftSliceStream : IDisposable
{
    const int QueueCapacity = 64;

    readonly Func<IntPtr, NativeProgressCallback, IntPtr, IntPtr> _nativeParse;
    readonly string _nativeName;
    readonly IProgress<MftScanProgress>? _progress;
    readonly ParseThreadAllowance? _allowance;
    readonly CancellationToken _cancellationToken;
    readonly BlockingCollection<MftParsedSlice> _queue = new(QueueCapacity);

    GCHandle _self;
    Thread? _parseThread;
    IntPtr _result;
    Exception? _failure;
    MftExtensionDataSize[] _extensionDataSizes = [];
    volatile bool _stop;
    bool _finished;

    /// <summary>Prepares the stream; <see cref="Start" /> runs the parse.</summary>
    /// <param name="nativeParse">
    ///     The streaming native call: given the control block, the progress callback and the sink, it parses and
    ///     returns the native result.
    /// </param>
    /// <param name="nativeName">The native entry point, named when it returns null.</param>
    /// <param name="progress">Receives native parse progress.</param>
    /// <param name="allowance">The thread count the parse reads at every chunk; null uses every processor.</param>
    /// <param name="cancellationToken">Stops the native parse.</param>
    internal MftSliceStream(Func<IntPtr, NativeProgressCallback, IntPtr, IntPtr> nativeParse, string nativeName,
        IProgress<MftScanProgress>? progress, ParseThreadAllowance? allowance, CancellationToken cancellationToken)
    {
        _nativeParse = nativeParse;
        _nativeName = nativeName;
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
        _parseThread = new Thread(ParseOnThread) { IsBackground = true, Name = "MFT streaming parse" };
        _parseThread.Start();
    }

    /// <summary>The parsed slices in record order, ending when the parse returns. Return each slice once consumed.</summary>
    internal IEnumerable<MftParsedSlice> Slices() => _queue.GetConsumingEnumerable();

    /// <summary>
    ///     Waits for the parse to return, then throws what stopped it: a callback failure, or the result's own
    ///     cancellation, rejection or failure, with the same exception types a whole-result parse throws.
    /// </summary>
    /// <returns>The allocated records the parse passed over because their fixup was invalid.</returns>
    internal long Finish()
    {
        (_parseThread ?? throw new InvalidOperationException("The stream was never started.")).Join();
        _finished = true;
        if (_failure is { } failure)
        {
            ExceptionDispatchInfoThrow(failure);
        }

        var result = _result;
        _result = IntPtr.Zero;
        if (result == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{_nativeName} returned null");
        }

        // The constructor frees a failed result and throws its classified exception.
        using var parsed = new MftResult(result, _cancellationToken);
        return checked((long)parsed.InvalidFixupRecordCount);
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
                    Slice = (IntPtr)slice,
                    Extensions = (IntPtr)extensions,
                    Context = GCHandle.ToIntPtr(_self)
                };
                _result = _nativeParse(controlAddress, registration.Callback, (IntPtr)(&sink));
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
        // The context is the handle Start allocated, which outlives the parse.
        if (GCHandle.FromIntPtr(context).Target is not MftSliceStream stream)
        {
            return 1;
        }

        try
        {
            if (stream._stop)
            {
                return 1;
            }

            var slice = new MftParsedSlice(checked((int)entryCount), checked((int)stringUnits));
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
        // The context is the handle Start allocated, which outlives the parse.
        if (GCHandle.FromIntPtr(context).Target is not MftSliceStream stream)
        {
            return 1;
        }

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
