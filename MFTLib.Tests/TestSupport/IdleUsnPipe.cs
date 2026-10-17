using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

internal sealed class IdleUsnPipe : IDisposable
{
    readonly NamedPipeServerStream _writer;
    internal EventWaitHandle BeforeIssue { get; } = new(false, EventResetMode.ManualReset);
    internal EventWaitHandle ContinueIssue { get; } = new(false, EventResetMode.ManualReset);
    internal EventWaitHandle Issued { get; } = new(false, EventResetMode.ManualReset);
    internal EventWaitHandle CancelAttempted { get; } = new(false, EventResetMode.ManualReset);
    internal SafeFileHandle Handle { get; private set; } = new(IntPtr.Zero, true);

    IdleUsnPipe(string name)
    {
        _writer = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal const int NeverGate = int.MaxValue;

    /// <summary>True when the last cancel of the pipe found a pending read, false when it came before one existed.</summary>
    internal static bool CancelFoundRequest => NativeTestHooks.NativeGetUsnWatchCancelFoundRequest() != 0;

    /// <summary>Native cancels (C) and result frees (F) of the armed pipe in call order, e.g. "FFCF".</summary>
    internal static string CallOrder()
    {
        var calls = new int[16];
        var count = NativeTestHooks.NativeGetUsnCallLog(calls, calls.Length);
        return string.Concat(calls.Take(count).Select(call => call == 1 ? 'C' : 'F'));
    }

    internal static async Task<IdleUsnPipe> CreateAsync(int gateReadNumber = NeverGate)
    {
        var name = $"mftlib-idle-usn-{Guid.NewGuid():N}";
        var pipe = new IdleUsnPipe(name);
        try
        {
            var connected = pipe._writer.WaitForConnectionAsync();
            pipe.Handle.Dispose();
            pipe.Handle = OpenReadPipe($@"\\.\pipe\{name}", 0x80000000, 0,
                IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (pipe.Handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            await connected.WaitAsync(TimeSpan.FromSeconds(10));
            NativeTestHooks.NativeSetUsnWatchPipe(pipe.Handle, pipe.BeforeIssue.SafeWaitHandle,
                pipe.ContinueIssue.SafeWaitHandle, pipe.Issued.SafeWaitHandle, pipe.CancelAttempted.SafeWaitHandle,
                gateReadNumber);
            FileUtilities.Hooks.GetWatchVolumeHandle = pipe.BorrowHandle;
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    internal SafeFileHandle BorrowHandle(string? _ = null) => new(Handle.DangerousGetHandle(), false);

    internal async Task SendAsync(byte[] readBuffer)
    {
        await _writer.WriteAsync(readBuffer);
        await _writer.FlushAsync();
    }

    internal Task SendEmptyBatchAsync(long nextUsn) => SendAsync(BitConverter.GetBytes(nextUsn));

    /// <summary>Closes the writing end, so a read that is pending or issued afterwards fails with a broken pipe.</summary>
    internal void Break() => _writer.Dispose();

    internal static async Task AwaitSignalAsync(EventWaitHandle signal)
    {
        Assert.IsTrue(await Task.Run(() => signal.WaitOne(TimeSpan.FromSeconds(10))),
            "Native watch did not reach the expected synchronization gate.");
    }

    internal void UnblockForCleanup()
    {
        ContinueIssue.Set();
        _writer.Dispose();
    }

    public void Dispose()
    {
        UnblockForCleanup();
        Handle.Dispose();
        BeforeIssue.Dispose();
        ContinueIssue.Dispose();
        Issued.Dispose();
        CancelAttempted.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    static extern SafeFileHandle OpenReadPipe(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
