namespace MFTLib;

/// <summary>
///     Injectable abstraction over the self-elevation decision so consumers can test
///     their elevation logic - including the already-elevated branch - without triggering
///     real UAC. Use <see cref="ElevationUtilities.DefaultProvider" /> in production and a
///     fake in tests.
/// </summary>
public interface IElevationProvider
{
    /// <summary>Determines whether the current process has an elevated access token.</summary>
    /// <returns>true when the current process is elevated; otherwise, false.</returns>
    bool IsElevated();
    /// <summary>Determines whether this process can request UAC elevation.</summary>
    /// <returns>true when self-elevation is supported; otherwise, false.</returns>
    bool CanSelfElevate();
    /// <summary>Runs the current application elevated with the supplied command-line arguments.</summary>
    /// <param name="arguments">Arguments passed to the elevated child process.</param>
    /// <param name="timeoutMs">
    ///     Maximum time, in milliseconds, to wait for the elevated child. The default is 60,000.
    /// </param>
    /// <returns>true when the elevated child exits with code zero; false when it cannot be started, the user declines elevation, it exits non-zero, or the timeout elapses.</returns>
    bool TryRunElevated(string arguments, int timeoutMs = 60000);
}
