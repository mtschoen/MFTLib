using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MFTLib.NativeInterop;

/// <summary>
///     One Windows Plug and Play registration for volume interface arrivals
///     (<c>CM_Register_Notification</c> filtered on <c>GUID_DEVINTERFACE_VOLUME</c>, Windows 8 and later,
///     no window or service handle, no elevation). The callback runs on a system thread, so it only
///     queues the arrival action to the thread pool and returns: it takes no lock, which is what makes
///     <see cref="Dispose" /> safe from any thread except a notification callback, since
///     <c>CM_Unregister_Notification</c> waits for pending callbacks. The action is reached through a
///     GC handle passed as the registration's context and freed only after unregistration.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe partial class VolumeArrivalRegistration : IDisposable
{
    /// <summary><c>GUID_DEVINTERFACE_VOLUME</c>, the device interface class every volume exposes.</summary>
    internal static readonly Guid VolumeInterfaceClass = new("53F5630D-B6BF-11D0-94F2-00A0C91EFB8B");

    const uint FilterTypeDeviceInterface = 0;
    const uint ActionDeviceInterfaceArrival = 0;
    const uint ConfigurationSuccess = 0;
    const uint GeneralFailure = 31;

    GCHandle _context;
    IntPtr _notification;

    VolumeArrivalRegistration(GCHandle context, IntPtr notification)
    {
        _context = context;
        _notification = notification;
    }

    /// <summary>The trampoline the registration passes as its callback; a test calls it as the system would.</summary>
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint, uint> Callback => &OnNotification;

    /// <summary>
    ///     Registers for volume interface arrivals; each one queues <paramref name="onArrival" /> to the
    ///     thread pool. Throws <see cref="Win32Exception" /> carrying the <c>CONFIGRET</c> code, mapped
    ///     to a Win32 error, when the registration fails.
    /// </summary>
    internal static VolumeArrivalRegistration Register(Action onArrival)
    {
        var context = GCHandle.Alloc(onArrival);
        var filter = new NotifyFilter
        {
            Size = (uint)sizeof(NotifyFilter),
            FilterType = FilterTypeDeviceInterface,
            ClassGuid = VolumeInterfaceClass
        };
        var result = CM_Register_Notification(&filter, GCHandle.ToIntPtr(context), Callback, out var notification);
        if (result != ConfigurationSuccess)
        {
            context.Free();
            throw new Win32Exception((int)CM_MapCrToWin32Err(result, GeneralFailure),
                $"CM_Register_Notification for volume arrivals failed with CONFIGRET {result}.");
        }

        return new VolumeArrivalRegistration(context, notification);
    }

    /// <summary>
    ///     Unregisters, which waits for any callback in flight, then frees the context. Idempotent.
    ///     Never call it from inside a notification callback.
    /// </summary>
    public void Dispose()
    {
        var notification = Interlocked.Exchange(ref _notification, IntPtr.Zero);
        if (notification == IntPtr.Zero)
        {
            return;
        }

        _ = CM_Unregister_Notification(notification);
        _context.Free();
    }

    /// <summary>
    ///     Queues the context's action for an interface arrival and ignores every other action. Never
    ///     throws across the unmanaged boundary, and always answers <c>ERROR_SUCCESS</c>, as the
    ///     callback contract requires for every action but a query-remove.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint OnNotification(IntPtr notification, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        if (action == ActionDeviceInterfaceArrival &&
            GCHandle.FromIntPtr(context).Target is Action onArrival)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static queued => queued(), onArrival, preferLocal: false);
        }

        return 0;
    }

    /// <summary>
    ///     <c>CM_NOTIFY_FILTER</c>: four <c>DWORD</c>s, then a union whose largest member is
    ///     <c>WCHAR InstanceId[MAX_DEVICE_ID_LEN]</c> (200 characters), 416 bytes in all on x64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Register_Notification")]
    private static partial uint CM_Register_Notification(NotifyFilter* filter, IntPtr context,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint, uint> callback, out IntPtr notification);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_MapCrToWin32Err")]
    private static partial uint CM_MapCrToWin32Err(uint configurationResult, uint defaultError);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification")]
    private static partial uint CM_Unregister_Notification(IntPtr notification);
}
