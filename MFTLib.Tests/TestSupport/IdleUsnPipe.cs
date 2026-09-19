using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

internal sealed class IdleUsnPipe : IDisposable
{
    readonly NamedPipeServerStream _writer;
    internal readonly EventWaitHandle BeforeIssue = new(false, EventResetMode.ManualReset);
    internal readonly EventWaitHandle ContinueIssue = new(false, EventResetMode.ManualReset);
    internal readonly EventWaitHandle Issued = new(false, EventResetMode.ManualReset);
    internal SafeFileHandle Handle { get; private set; } = new(IntPtr.Zero, true);

    IdleUsnPipe(string name)
    {
        _writer = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal static async Task<IdleUsnPipe> CreateAsync(int gateReadNumber)
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
            MFTLibNative.NativeSetUsnWatchPipe(pipe.Handle, pipe.BeforeIssue.SafeWaitHandle,
                pipe.ContinueIssue.SafeWaitHandle, pipe.Issued.SafeWaitHandle, gateReadNumber);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    internal SafeFileHandle BorrowHandle() => new(Handle.DangerousGetHandle(), false);

    internal async Task SendEmptyBatchAsync(long nextUsn)
    {
        await _writer.WriteAsync(BitConverter.GetBytes(nextUsn));
        await _writer.FlushAsync();
    }

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
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    static extern SafeFileHandle OpenReadPipe(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
