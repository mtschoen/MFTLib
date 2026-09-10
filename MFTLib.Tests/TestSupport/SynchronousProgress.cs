namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A synchronous <see cref="IProgress{T}" /> that records every report immediately, unlike
///     <see cref="Progress{T}" />, which posts through a synchronization context and so cannot
///     be asserted on deterministically right after the call that triggered it.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value)
    {
        callback(value);
    }
}
