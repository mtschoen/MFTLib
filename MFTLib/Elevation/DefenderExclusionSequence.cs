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

    // The system Defender module, imported by absolute path so a module planted in a per-user PSModulePath
    // directory cannot be autoloaded into the elevated session.
    internal const string ImportSystemDefenderModule =
        @"Import-Module -Name (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules\Defender\Defender.psd1') -ErrorAction Stop";

    /// <summary>
    ///     The absolute-path import of the system Defender module, then one module-qualified
    ///     <c>Defender\Add-MpPreference -ExclusionProcess</c> per name, each name a single-quoted literal.
    /// </summary>
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

        return string.Join("; ", processNames
            .Select(name => $"Defender\\Add-MpPreference -ExclusionProcess '{name}' -ErrorAction Stop")
            .Prepend(ImportSystemDefenderModule));
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

    /// <summary>
    ///     Runs <paramref name="script" /> in Windows PowerShell as <c>-NoProfile -NonInteractive -Command</c>; true only
    ///     for exit code zero within <see cref="PowerShellTimeout" />. The elevated launch goes through ShellExecute, which
    ///     ignores <see cref="ProcessStartInfo.ArgumentList" />, so its arguments are one quoted string.
    /// </summary>
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
            startInfo.Arguments = string.Join(' ', ["-NoProfile", "-NonInteractive", "-Command", QuoteArgument(script)]);
        }
        else
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
        }

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

    // One command-line argument by the Windows C runtime rules: wrapped in double quotes, with each embedded quote
    // escaped and the backslashes in front of a quote (or the closing quote) doubled.
    internal static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }

            backslashes = 0;
            quoted.Append(character);
        }

        quoted.Append('\\', backslashes * 2);
        return quoted.Append('"').ToString();
    }
}
