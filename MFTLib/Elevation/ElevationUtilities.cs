using System.Runtime.InteropServices;

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

    /// <summary>
    ///     Adds Windows Defender process exclusions so scanning is not slowed: directly when this process is elevated,
    ///     through <see cref="IElevationProvider.TryRunElevated" /> when it can relaunch itself, and through an elevated
    ///     PowerShell otherwise. Returns false on a platform other than Windows without launching anything.
    /// </summary>
    /// <param name="processNames">Plain executable file names such as <c>app.exe</c>: letters, digits, '.', '-', '_', '+' and inner spaces. Anything else is refused, so a name can never inject a command.</param>
    /// <param name="relaunchArguments">The arguments of the elevated relaunch. The relaunched process calls this method again with the same names; being elevated, it runs the exclusions directly.</param>
    /// <param name="elevation">The elevation behavior to use; <see cref="DefaultProvider" /> when null.</param>
    /// <returns>true when the exclusions were applied; false when the relaunch or PowerShell cannot start, the user declines elevation, the run exits non-zero or times out, or the platform is not Windows.</returns>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    /// <exception cref="ArgumentException">The name list is empty or a name is not a plain file name.</exception>
    /// <remarks>
    ///     A PowerShell run is killed after 30 seconds and a self-elevated child gets 60 seconds, so the outer wait
    ///     always outlasts the inner one.
    /// </remarks>
    public static bool TryAddDefenderProcessExclusions(
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> relaunchArguments,
        IElevationProvider? elevation = null)
    {
        return DefenderExclusionSequence.Run(
            processNames,
            relaunchArguments,
            elevation ?? DefaultProvider,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            DefenderExclusionSequence.RunnerOverride ?? DefenderExclusionSequence.Production);
    }
}
