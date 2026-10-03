using System.Diagnostics.CodeAnalysis;

namespace TestProgram;

/// <summary>What one run does to each drive.</summary>
internal enum ProgramMode
{
    /// <summary>Finds every .git directory in the drive's MFT.</summary>
    FindGit,

    /// <summary>Scans the drive into a block through the elevated broker, with no FileIndex.</summary>
    ScanDrive,

    /// <summary>Reads every MFT record with resolved paths.</summary>
    ReadRecords,

    /// <summary>Reports the USN journal's cursor and sizing.</summary>
    UsnQuery,

    /// <summary>Arms a cursor, reads every MFT record, then reads the journal from the armed cursor.</summary>
    UsnRead,

    /// <summary>Watches the USN journal from its current cursor for a number of seconds.</summary>
    UsnWatch
}

/// <summary>
///     The parsed command line: an optional mode name, then drive letters, then mode options. A
///     first argument that names no mode is a drive letter and selects <see cref="ProgramMode.FindGit" />.
/// </summary>
internal sealed record TestProgramArguments(ProgramMode Mode, IReadOnlyList<string> Drives, int WatchSeconds)
{
    internal const string DefaultDrive = "G";
    internal const int DefaultWatchSeconds = 10;
    internal const int DefaultElevationTimeoutMilliseconds = 60000;
    internal const string SecondsOption = "--seconds";

    internal static string Usage =>
        "Usage: TestProgram [mode] [drive ...] [--seconds N]" + Environment.NewLine +
        "  modes: " + string.Join(", ", ProgramModes.Names.Keys) + " (default find-git)" + Environment.NewLine +
        $"  drive defaults to {DefaultDrive}; --seconds applies to usn-watch (default {DefaultWatchSeconds})";

    /// <summary>
    ///     Scanning through the broker is the one mode that runs unelevated: the broker is the
    ///     elevated process, and it asks for elevation itself.
    /// </summary>
    internal bool RequiresElevation => Mode != ProgramMode.ScanDrive;

    /// <summary>The wait for the elevated copy: the default plus every drive's requested watch time.</summary>
    internal int ElevationTimeoutMilliseconds
    {
        get
        {
            var watchMilliseconds = Mode == ProgramMode.UsnWatch ? (long)Drives.Count * WatchSeconds * 1000 : 0;
            return (int)Math.Min(int.MaxValue, DefaultElevationTimeoutMilliseconds + watchMilliseconds);
        }
    }

    internal static bool TryParse(string[] arguments, out TestProgramArguments parsed,
        [NotNullWhen(false)] out string? error)
    {
        var mode = ProgramMode.FindGit;
        var start = 0;
        if (arguments.Length > 0 && ProgramModes.Names.TryGetValue(arguments[0], out var named))
        {
            mode = named;
            start = 1;
        }

        var drives = new List<string>();
        var watchSeconds = DefaultWatchSeconds;
        for (var index = start; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument == SecondsOption)
            {
                if (mode != ProgramMode.UsnWatch)
                {
                    return Fail($"{SecondsOption} applies only to usn-watch.", out parsed, out error);
                }

                if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out watchSeconds) ||
                    watchSeconds <= 0)
                {
                    return Fail($"{SecondsOption} needs a positive whole number.", out parsed, out error);
                }

                index++;
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                return Fail($"Unknown option {argument}.", out parsed, out error);
            }
            else
            {
                drives.Add(argument);
            }
        }

        parsed = new TestProgramArguments(mode, drives.Count > 0 ? drives : [DefaultDrive], watchSeconds);
        error = null;
        return true;
    }

    static bool Fail(string message, out TestProgramArguments parsed, [NotNullWhen(false)] out string? error)
    {
        parsed = new TestProgramArguments(ProgramMode.FindGit, [DefaultDrive], DefaultWatchSeconds);
        error = message;
        return false;
    }
}

internal static class ProgramModes
{
    internal static readonly Dictionary<string, ProgramMode> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["find-git"] = ProgramMode.FindGit,
        ["scan-drive"] = ProgramMode.ScanDrive,
        ["read-records"] = ProgramMode.ReadRecords,
        ["usn-query"] = ProgramMode.UsnQuery,
        ["usn-read"] = ProgramMode.UsnRead,
        ["usn-watch"] = ProgramMode.UsnWatch
    };
}
