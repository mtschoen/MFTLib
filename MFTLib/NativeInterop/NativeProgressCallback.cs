using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>
///     The progress pair a native parse export takes: an unmanaged function pointer and the context
///     it receives back on every report. The default value is "no progress"; native code then never
///     calls back.
/// </summary>
readonly unsafe struct NativeProgressCallback(IntPtr function, IntPtr context)
{
    internal IntPtr Function { get; } = function;

    internal IntPtr Context { get; } = context;

    /// <summary>
    ///     Reports through the function pointer exactly as native code would, so a substituted parse
    ///     drives the same adapter a real one does. Does nothing for the default value.
    /// </summary>
    internal void Invoke(ulong recordsScanned, ulong totalRecords, double elapsedMs)
    {
        if (Function == IntPtr.Zero)
        {
            return;
        }

        ((delegate* unmanaged[Cdecl]<IntPtr, ulong, ulong, double, void>)Function)(Context, recordsScanned,
            totalRecords, elapsedMs);
    }
}

/// <summary>
///     Pins an <see cref="IProgress{T}" /> for one native parse and exposes it as a
///     <see cref="NativeProgressCallback" />: one static trampoline as the function and a GC handle to
///     the progress as the context. Dispose after the parse returns; native code never keeps either.
/// </summary>
sealed unsafe class NativeProgressRegistration : IDisposable
{
    GCHandle _progress;

    internal NativeProgressRegistration(IProgress<MftScanProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        _progress = GCHandle.Alloc(progress);
        delegate* unmanaged[Cdecl]<IntPtr, ulong, ulong, double, void> trampoline = &Report;
        Callback = new NativeProgressCallback((IntPtr)trampoline, GCHandle.ToIntPtr(_progress));
    }

    internal NativeProgressCallback Callback { get; }

    public void Dispose()
    {
        if (_progress.IsAllocated)
        {
            _progress.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Report(IntPtr context, ulong recordsScanned, ulong totalRecords, double elapsedMs)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is IProgress<MftScanProgress> progress)
            {
                progress.Report(new MftScanProgress((long)recordsScanned, (long)totalRecords,
                    TimeSpan.FromMilliseconds(elapsedMs)));
            }
        }
        catch
        {
            // Never throw across the unmanaged boundary: an exception leaving an
            // UnmanagedCallersOnly method ends the process. This runs synchronously on the native
            // parse thread, so a malformed sample (a NaN elapsedMs) or a throwing consumer drops
            // the sample instead of aborting the parse. That is the documented contract of the
            // progress parameter of MftVolume.ReadRecordBatches, pinned by
            // MftScanProgressTests and MftVolumeTests.
        }
    }
}
