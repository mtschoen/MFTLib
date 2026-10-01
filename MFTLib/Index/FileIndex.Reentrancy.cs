namespace MFTLib.Index;

/// <summary>
///     The guard against a lifecycle call made from inside a <see cref="Changed" />,
///     <see cref="WatchFaulted" /> or <see cref="WatchStateChanged" /> handler. A handler runs on
///     a drive's pump, or on a scan or lifecycle call that holds a drive's lifecycle gate, and a
///     lifecycle call can wait for a pump that is itself blocked in a handler, so two handlers stopping each other's drives deadlock. Every
///     raise site delivers through <see cref="Deliver{TArgument}" />, which sets an
///     <see cref="AsyncLocal{T}" /> marker naming this index for exactly the handler invocation;
///     the marker flows with the handler's execution context, into its awaits and any work it
///     starts, and its <see cref="DeliveryMarker.Active" /> flag is cleared when the invocation
///     returns, so work queued from a handler that runs afterwards is allowed.
/// </summary>
public sealed partial class FileIndex
{
    static readonly AsyncLocal<DeliveryMarker?> s_delivery = new();

    /// <summary>Invokes one handler with this index's delivery marker set for the duration of the call.</summary>
    void Deliver<TArgument>(Action<TArgument> handler, TArgument argument)
    {
        var previous = s_delivery.Value;
        var marker = new DeliveryMarker(this, previous);
        s_delivery.Value = marker;
        try
        {
            handler(argument);
        }
        finally
        {
            marker.End();
            s_delivery.Value = previous;
        }
    }

    /// <summary>
    ///     Invokes every subscriber of a multicast event in order, each under its own marker that goes
    ///     inactive when that subscriber returns. A subscriber's exception stops the later ones, as
    ///     a plain multicast invocation would.
    /// </summary>
    void DeliverToEach<TArgument>(Action<TArgument> subscribers, TArgument argument)
    {
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            Deliver((Action<TArgument>)subscriber, argument);
        }
    }

    /// <summary>
    ///     The exception a lifecycle call named <paramref name="operation" /> fails with when it is
    ///     made from inside one of this index's handlers, or null when the call is allowed.
    /// </summary>
    InvalidOperationException? RejectInsideHandler(string operation)
    {
        return IsInsideHandlerOfThisIndex()
            ? new InvalidOperationException(
                $"FileIndex.{operation} was called from inside a Changed, WatchFaulted or WatchStateChanged " +
                "handler; it can wait for a watch pump that is blocked in a handler. Queue the call to run " +
                "after the handler returns, for example with Task.Run.")
            : null;
    }

    /// <summary>
    ///     Whether an active delivery of this index is on the execution context's chain. The chain
    ///     keeps the outer markers, so a handler of another index that a handler of this one
    ///     triggered synchronously is still recognized as inside this index's handler.
    /// </summary>
    bool IsInsideHandlerOfThisIndex()
    {
        for (var marker = s_delivery.Value; marker is not null; marker = marker.Outer)
        {
            if (marker.Active && ReferenceEquals(marker.Owner, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An already faulted task for a rejected call, or null when the call is allowed.</summary>
    Task? RejectedTask(string operation) =>
        RejectInsideHandler(operation) is { } rejection ? Task.FromException(rejection) : null;

    /// <summary>The typed counterpart of <see cref="RejectedTask(string)" />.</summary>
    Task<TResult>? RejectedTask<TResult>(string operation) =>
        RejectInsideHandler(operation) is { } rejection ? Task.FromException<TResult>(rejection) : null;

    sealed class DeliveryMarker(FileIndex owner, DeliveryMarker? outer)
    {
        volatile bool _active = true;

        public DeliveryMarker? Outer { get; } = outer;

        public FileIndex Owner { get; } = owner;

        public bool Active => _active;

        public void End() => _active = false;
    }
}
