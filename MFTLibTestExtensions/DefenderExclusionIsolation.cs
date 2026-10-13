namespace MFTLibTestExtensions;

/// <summary>Opt-in protection against a test starting a real PowerShell, or changing real Windows Defender settings, through <see cref="MFTLib.ElevationUtilities.TryAddDefenderProcessExclusions" />.</summary>
public static class DefenderExclusionIsolation
{
    /// <summary>
    ///     Replaces every PowerShell launch of <see cref="MFTLib.ElevationUtilities.TryAddDefenderProcessExclusions" />
    ///     with a recorded call that reports <paramref name="succeeds" />, until the returned scope is disposed.
    ///     The elevation provider the code under test passes is not affected: a fake provider decides the elevated,
    ///     self-elevating or PowerShell branch, and only the last one reaches this script.
    /// </summary>
    /// <param name="succeeds">Whether each scripted PowerShell run reports exit code zero.</param>
    /// <returns>A process-global scope that records each launch and restores the real launcher on disposal.</returns>
    /// <exception cref="InvalidOperationException">Another scope is active.</exception>
    /// <remarks>Use a nonparallel fixture for the whole scope lifetime. Nested and overlapping scopes are rejected. Repeated disposal is harmless.</remarks>
    public static ScriptedPowerShell ScriptPowerShell(bool succeeds = true)
    {
        return ScriptedPowerShell.Create(succeeds);
    }
}
