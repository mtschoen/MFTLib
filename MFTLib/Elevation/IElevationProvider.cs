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
    /// <param name="arguments">Arguments passed to the elevated child process, one element per argument; each is quoted for the child's command line.</param>
    /// <param name="timeout">
    ///     Maximum time to wait for the elevated child; <see cref="ElevationUtilities.DefaultElevatedTimeout" /> is a sensible choice.
    ///     Must be between 0 and <see cref="int.MaxValue" /> milliseconds, or <see cref="Timeout.InfiniteTimeSpan" />.
    /// </param>
    /// <returns>true when the elevated child exits with code zero; false when it cannot be started, the user declines elevation, it exits non-zero, the timeout is unsupported, or the timeout elapses.</returns>
    bool TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout);
}
