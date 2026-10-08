namespace MFTLib;

/// <summary>
///     Entry point to elevation checks and the UAC self-relaunch helper that raw volume access needs:
///     <see cref="DefaultProvider" /> performs them, and <see cref="DefaultElevatedTimeout" /> is the usual wait.
/// </summary>
public static class ElevationUtilities
{
    /// <summary>The wait <see cref="IElevationProvider.TryRunElevated" /> callers use when they have no reason to choose another: 60 seconds.</summary>
    public static readonly TimeSpan DefaultElevatedTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Production <see cref="IElevationProvider" />: the real Windows token check and UAC relaunch.
    ///     Consumers default to this and inject a fake in tests.
    /// </summary>
    public static IElevationProvider DefaultProvider { get; } = new DefaultElevationProvider();
}
