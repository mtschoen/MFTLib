using MFTLib.NativeInterop;

namespace MFTLib.Index;

/// <summary>
///     Fans the operating system's volume arrivals out to the scans whose automatic recovery waits on
///     an absent volume. The platform registration is made lazily by the first subscriber and released
///     when the last one leaves. An arrival names no root: a volume interface can arrive seconds
///     before its drive letter mounts, so each subscriber probes its own volume.
/// </summary>
/// <param name="register">Registers the platform notification that calls the given action on each arrival.</param>
internal sealed class VolumeArrivalSignal(Func<Action, IDisposable> register)
{
    readonly Lock _gate = new();
    readonly List<Subscription> _subscribers = [];
    IDisposable? _registration;

    /// <summary>
    ///     The process's signal: Plug and Play volume interface arrivals on Windows, and none on other
    ///     platforms, where recovery retries on its schedule only.
    /// </summary>
    internal static VolumeArrivalSignal Platform { get; } = new(RegisterPlatform);

    static IDisposable RegisterPlatform(Action onArrival) =>
        OperatingSystem.IsWindows() ? VolumeArrivalRegistration.Register(onArrival) : NoRegistration.Instance;

    /// <summary>
    ///     Calls <paramref name="onArrival" /> on every arrival until the returned subscription is
    ///     disposed. The first subscriber registers the platform notification, and its failure
    ///     propagates to that subscriber. Disposal never runs on a notification callback, since the
    ///     platform callback only queues the arrival.
    /// </summary>
    internal IDisposable Subscribe(Action onArrival)
    {
        var subscription = new Subscription(this, onArrival);
        lock (_gate)
        {
            _registration ??= register(Raise);
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Delivers one arrival to every current subscriber, on the calling thread.</summary>
    internal void Raise()
    {
        Subscription[] subscribers;
        lock (_gate)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Deliver();
        }
    }

    void Unsubscribe(Subscription subscription)
    {
        IDisposable? released = null;
        lock (_gate)
        {
            if (_subscribers.Remove(subscription) && _subscribers.Count == 0)
            {
                released = _registration;
                _registration = null;
            }
        }

        released?.Dispose();
    }

    sealed class Subscription(VolumeArrivalSignal signal, Action onArrival) : IDisposable
    {
        int _disposed;

        public void Deliver()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                onArrival();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                signal.Unsubscribe(this);
            }
        }
    }

    sealed class NoRegistration : IDisposable
    {
        internal static NoRegistration Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
