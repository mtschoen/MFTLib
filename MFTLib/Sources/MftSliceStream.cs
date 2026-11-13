using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>A streaming native export bound to its input, and the entry point a null result names.</summary>
/// <param name="Call">Given the control block, the progress callback and the sink, parses and returns the native result.</param>
/// <param name="NativeName">The native entry point.</param>
readonly record struct MftStreamingParse(Func<IntPtr, NativeProgressCallback, IntPtr, IntPtr> Call, string NativeName);

/// <summary>
///     Runs a streaming native parse, of a dump or a live volume, on a thread of its own and hands its slices to the
///     consuming thread through a bounded queue: the consumer writes rows while later chunks are still read and
///     parsed, and a slow consumer holds the parser back instead of growing memory. The native callbacks never throw:
///     a failure in one is kept, stops the parse, and is what <see cref="Finish" /> throws.
/// </summary>
sealed unsafe class MftSliceStream : IMftSliceSource
{
    const int QueueCapacity = 64;

    readonly MftStreamingParse _nativeParse;
    readonly IProgress<MftScanProgress>? _progress;
    readonly ParseThreadAllowance? _allowance;
    readonly IDisposable? _owned;
    readonly CancellationToken _cancellationToken;
    readonly BlockingCollection<MftParsedSlice> _queue = new(QueueCapacity);

    GCHandle _self;
    Thread? _parseThread;
    MftResult? _result;
    Exception? _failure;
    MftExtensionDataSize[] _extensionDataSizes = [];
    volatile bool _stopRequested;
    bool _joined;

    /// <summary>Prepares the stream; enumerating <see cref="Slices" /> runs the parse.</summary>
    /// <param name="nativeParse">The streaming export and its name.</param>
    /// <param name="progress">Receives native parse progress on the parse thread.</param>
    /// <param name="allowance">The thread count the parse reads at every chunk; null uses every processor.</param>
    /// <param name="owned">Released once the parse has returned, such as the volume it reads; null owns nothing.</param>
    /// <param name="validation">The checks the parsed rows pass: a dump's are untrusted.</param>
    /// <param name="cancellationToken">Stops the native parse.</param>
    internal MftSliceStream(MftStreamingParse nativeParse, IProgress<MftScanProgress>? progress, ParseThreadAllowance? allowance, IDisposable? owned,
        MftRowValidation validation, CancellationToken cancellationToken)
    {
        Validation = validation;
        _nativeParse = nativeParse;
        _progress = progress;
        _allowance = allowance;
        _owned = owned;
        _cancellationToken = cancellationToken;
    }

    public MftRowValidation Validation { get; }

    /// <summary>Starts the parse thread and returns the slices in record order, ending when the parse returns.</summary>
    /// <exception cref="InvalidOperationException">The stream was already started.</exception>
    public IEnumerable<MftParsedSlice> Slices()
    {
        if (_parseThread is not null)
        {
            throw new InvalidOperationException("The parse stream was already started.");
        }

        MFTLibNative.EnsureCompatibleNativeAbi();
        _self = GCHandle.Alloc(this);
        _parseThread = new Thread(ParseOnThread) { IsBackground = true, Name = "MFT streaming parse" };
        _parseThread.Start();
        return _queue.GetConsumingEnumerable();
    }

    /// <summary>
    ///     Waits for the parse to return, then throws what stopped it: a callback failure, or the result's own
    ///     cancellation, rejection or failure, with the exception types a whole-result parse throws.
    /// </summary>
    public MftSliceCompletion Finish()
    {
        Join();
        if (_failure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var result = _result ?? throw new InvalidOperationException("The parse stream was never started.");
        return new MftSliceCompletion(checked((long)result.InvalidFixupRecordCount), _extensionDataSizes);
    }

    /// <summary>Stops a parse the consumer abandoned, waits for it, then releases the result and what it owns.</summary>
    public void Dispose()
    {
        if (_parseThread is not null && !_joined)
        {
            _stopRequested = true;
            foreach (var slice in _queue.GetConsumingEnumerable())
            {
                slice.Return();
            }
        }

        Join();
        _result?.Dispose();
        _owned?.Dispose();
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _queue.Dispose();
    }

    void Join()
    {
        if (_parseThread is { } thread && !_joined)
        {
            thread.Join();
            _joined = true;
        }
    }

    void ParseOnThread()
    {
        try
        {
            delegate* unmanaged[Cdecl]<IntPtr, byte*, ulong, ushort*, ulong, int> slice = &OnSlice;
            delegate* unmanaged[Cdecl]<IntPtr, MftExtensionDataSize*, ulong, int> extensions = &OnExtensions;
            var sink = new MftParseSink
            {
                Slice = (IntPtr)slice,
                Extensions = (IntPtr)extensions,
                Context = GCHandle.ToIntPtr(_self)
            };
            var sinkAddress = (IntPtr)(&sink);
            _result = MftVolume.ParseToResult(
                (control, callback) => _nativeParse.Call(control, callback, sinkAddress),
                _nativeParse.NativeName, _progress, _allowance, _cancellationToken);
        }
        catch (Exception exception)
        {
            // A callback failure stopped the parse, which then reports cancelled: the callback's is the cause.
            _failure ??= exception;
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSlice(IntPtr context, byte* entries, ulong entryCount, ushort* strings, ulong stringUnits)
    {
        // The context is the handle Slices allocated, which outlives the parse.
        if (GCHandle.FromIntPtr(context).Target is not MftSliceStream stream)
        {
            return 1;
        }

        try
        {
            if (stream._stopRequested)
            {
                return 1;
            }

            stream._queue.Add(MftParsedSlice.CopyFromNative(entries, entryCount, strings, stringUnits));
            return stream._stopRequested ? 1 : 0;
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
        // The context is the handle Slices allocated, which outlives the parse.
        if (GCHandle.FromIntPtr(context).Target is not MftSliceStream stream)
        {
            return 1;
        }

        try
        {
            stream._extensionDataSizes = new ReadOnlySpan<MftExtensionDataSize>(sizes, checked((int)count)).ToArray();
            return stream._stopRequested ? 1 : 0;
        }
        catch (Exception exception)
        {
            stream._failure ??= exception;
            return 1;
        }
    }
}
