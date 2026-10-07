namespace MFTLib;

/// <summary>Delivers each report on the reporting thread, so the order a producer reports in is the order it is seen.</summary>
internal sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{
    readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value)
    {
        _handler(value);
    }
}
