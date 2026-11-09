using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.NativeInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The volume-arrival fan-out and its Windows registration. The fan-out is driven through its own
///     <see cref="VolumeArrivalSignal.Raise" />; the registration is made for real, and its callback is
///     invoked through the trampoline it hands the system, since no test can make a volume arrive.
/// </summary>
[TestClass]
public sealed class VolumeArrivalSignalTests
{
    public TestContext TestContext { get; set; } = null!;

    sealed class CountingRegistrations
    {
        public int Registered;
        public int Released;
        public Action? Raise;

        public IDisposable Register(Action onArrival)
        {
            Registered++;
            Raise = onArrival;
            return new Release(this);
        }

        sealed class Release(CountingRegistrations owner) : IDisposable
        {
            public void Dispose() => owner.Released++;
        }
    }

    [TestMethod]
    public void Subscribers_ShareOneRegistration_ReleasedWithTheLast()
    {
        var registrations = new CountingRegistrations();
        var signal = new VolumeArrivalSignal(registrations.Register);
        var first = 0;
        var second = 0;

        var firstSubscription = signal.Subscribe(() => first++);
        var secondSubscription = signal.Subscribe(() => second++);
        registrations.Raise!();
        firstSubscription.Dispose();
        firstSubscription.Dispose();
        signal.Raise();

        Assert.AreEqual(1, registrations.Registered);
        Assert.AreEqual(0, registrations.Released, "a remaining subscriber keeps the registration");
        Assert.AreEqual(1, first, "a disposed subscriber hears nothing more");
        Assert.AreEqual(2, second);

        secondSubscription.Dispose();
        Assert.AreEqual(1, registrations.Released);

        using var later = signal.Subscribe(() => { });
        Assert.AreEqual(2, registrations.Registered, "the next subscriber registers afresh");
    }

    [TestMethod]
    public void FailedRegistration_ReachesTheSubscriber_AndTheNextSubscriberRetries()
    {
        var attempts = 0;
        var signal = new VolumeArrivalSignal(_ =>
        {
            attempts++;
            return attempts == 1 ? throw new Win32Exception(31, "no notifications") : new CountingRegistrations().Register(() => { });
        });

        Assert.ThrowsExactly<Win32Exception>(() => signal.Subscribe(() => { }));
        using var subscription = signal.Subscribe(() => { });

        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public void PlatformSignal_SubscribesAndReleases()
    {
        using var subscription = VolumeArrivalSignal.Platform.Subscribe(() => { });
        Assert.IsNotNull(subscription);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void WindowsRegistration_RegistersAndUnregisters()
    {
        var registration = VolumeArrivalRegistration.Register(() => { });

        registration.Dispose();
        registration.Dispose();
        Assert.AreEqual(new Guid("53F5630D-B6BF-11D0-94F2-00A0C91EFB8B"), VolumeArrivalRegistration.VolumeInterfaceClass);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task WindowsCallback_QueuesArrivalsOnly()
    {
        var removed = false;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalContext = GCHandle.Alloc(new Action(() => removed = true));
        var arrivalContext = GCHandle.Alloc(new Action(arrived.SetResult));
        var notAnAction = GCHandle.Alloc("not an action");
        try
        {
            const uint interfaceArrival = 0;
            const uint interfaceRemoval = 1;
            Assert.AreEqual(0u, InvokeCallback(GCHandle.ToIntPtr(removalContext), interfaceRemoval));
            Assert.AreEqual(0u, InvokeCallback(GCHandle.ToIntPtr(notAnAction), interfaceArrival));
            Assert.AreEqual(0u, InvokeCallback(GCHandle.ToIntPtr(arrivalContext), interfaceArrival));

            await arrived.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.IsFalse(removed, "a removal is not an arrival");
        }
        finally
        {
            removalContext.Free();
            arrivalContext.Free();
            notAnAction.Free();
        }
    }

    [SupportedOSPlatform("windows")]
    static unsafe uint InvokeCallback(IntPtr context, uint action) =>
        VolumeArrivalRegistration.Callback(IntPtr.Zero, context, action, IntPtr.Zero, 0);
}
