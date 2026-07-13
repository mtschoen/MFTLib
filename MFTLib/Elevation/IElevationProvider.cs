namespace MFTLib;

/// <summary>
/// Injectable abstraction over the self-elevation decision so consumers can test
/// their elevation logic — including the already-elevated branch — without triggering
/// real UAC. Use <see cref="ElevationUtilities.DefaultProvider"/> in production and a
/// fake in tests.
/// </summary>
public interface IElevationProvider
{
    bool IsElevated();
    bool CanSelfElevate();
    bool TryRunElevated(string arguments, int timeoutMs = 60000);
}
