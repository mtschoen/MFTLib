using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MFTLib;

/// <summary>
///     The Windows Defender process-exclusion sequence behind <see cref="ElevationUtilities.TryAddDefenderProcessExclusions" />:
///     validate the names, then run directly, self-elevate or use an elevated PowerShell, whichever the process allows.
/// </summary>
sealed class DefenderExclusionSequence
{
    /// <summary>How long one PowerShell run may take before it is killed.</summary>
    internal static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What the elevated child gets beyond <see cref="PowerShellTimeout" /> to start and to exit.</summary>
    internal static readonly TimeSpan ElevatedChildSlack = TimeSpan.FromSeconds(30);

    /// <summary>The wait for a self-elevated child: always longer than the PowerShell run inside it.</summary>
    internal static readonly TimeSpan ElevatedTimeout = PowerShellTimeout + ElevatedChildSlack;

    /// <summary>The Windows PowerShell in the system directory, never a name left to the executable search order.</summary>
    internal static string PowerShellPath { get; } =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    internal delegate bool PowerShellRunner(string script, bool elevated);

    /// <summary>Replaces every PowerShell launch while set; test support only.</summary>
    internal static PowerShellRunner? RunnerOverride { get; set; }

    internal static PowerShellRunner Production { get; } = new DefenderExclusionSequence(new ElevationSeams()).RunPowerShell;

    readonly Func<ProcessStartInfo, Process?> _startProcess;
    readonly Func<Process, TimeSpan, bool> _waitForExit;
    readonly Action<Process> _killProcess;
    readonly Func<Process, int> _getExitCode;

    internal DefenderExclusionSequence(ElevationSeams seams)
    {
        _startProcess = seams.StartProcess ?? Process.Start;
        _waitForExit = seams.WaitForExit ?? WaitForExit;
        _killProcess = seams.KillProcess ?? Kill;
        _getExitCode = seams.GetExitCode ?? ExitCode;
    }

    internal static bool WaitForExit(Process process, TimeSpan timeout) => process.WaitForExit(timeout);

    internal static void Kill(Process process) => process.Kill();

    internal static int ExitCode(Process process) => process.ExitCode;

    internal static bool Run(
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> relaunchArguments,
        IElevationProvider elevation,
        bool isWindows,
        PowerShellRunner runPowerShell)
    {
        var script = BuildScript(processNames);
        ArgumentNullException.ThrowIfNull(relaunchArguments);
        if (!isWindows)
        {
            return false;
        }

        if (elevation.IsElevated())
        {
            return runPowerShell(script, false);
        }

        return elevation.CanSelfElevate()
            ? elevation.TryRunElevated(relaunchArguments, ElevatedTimeout)
            : runPowerShell(script, true);
    }

    /// <summary>One <c>Add-MpPreference -ExclusionProcess</c> per name, each name a single-quoted literal.</summary>
    internal static string BuildScript(IReadOnlyList<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        if (processNames.Count == 0)
        {
            throw new ArgumentException("At least one process name is required.", nameof(processNames));
        }

        foreach (var name in processNames)
        {
            RequirePlainFileName(name, nameof(processNames));
        }

        return string.Join("; ", processNames.Select(
            name => $"Add-MpPreference -ExclusionProcess '{name}' -ErrorAction Stop"));
    }

    // An allow-list, so no character that PowerShell treats specially can reach the script, on any platform.
    static void RequirePlainFileName(string? name, string parameterName)
    {
        var plain = !string.IsNullOrWhiteSpace(name)
            && name == name.Trim()
            && name.Any(char.IsLetterOrDigit)
            && name.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+' or ' ');
        if (!plain)
        {
            throw new ArgumentException(
                $"'{name}' is not a plain file name: use letters, digits, '.', '-', '_', '+' and inner spaces only.",
                parameterName);
        }
    }

    /// <summary>Runs <paramref name="script" /> in Windows PowerShell; true only for exit code zero within <see cref="PowerShellTimeout" />.</summary>
    internal bool RunPowerShell(string script, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = elevated,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        try
        {
            using var process = _startProcess(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!_waitForExit(process, PowerShellTimeout))
            {
                _killProcess(process);
                return false;
            }

            return _getExitCode(process) == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
