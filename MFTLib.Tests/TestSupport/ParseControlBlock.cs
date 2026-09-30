using System.Runtime.InteropServices;
using MFTLib.Interop;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A native MftParseControl block a test owns, at one address for its whole lifetime, plus
///     readers for the parse thread-count test hooks. Tests that use the hooks must be
///     class-level [DoNotParallelize]: the recorded counts are process-wide.
/// </summary>
sealed unsafe class ParseControlBlock : IDisposable
{
    readonly MftParseControl* _control = (MftParseControl*)NativeMemory.AllocZeroed((nuint)sizeof(MftParseControl));

    public ParseControlBlock(int allowance = 0)
    {
        Allowance = allowance;
    }

    public IntPtr Pointer => (IntPtr)_control;

    public int Allowance
    {
        get => Volatile.Read(ref _control->ParseThreadAllowance);
        set => Volatile.Write(ref _control->ParseThreadAllowance, value);
    }

    public void RequestCancel()
    {
        Volatile.Write(ref _control->CancelRequested, 1);
    }

    public void Dispose()
    {
        NativeMemory.Free(_control);
    }

    /// <summary>The thread count each chunk of the most recent native parse used, in order.</summary>
    public static uint[] ChunkThreadCounts()
    {
        var counts = new uint[4096];
        fixed (uint* first = counts)
        {
            var recorded = MFTLibNative.NativeGetChunkThreadCounts(first, (uint)counts.Length);
            return counts[..(int)recorded];
        }
    }
}
