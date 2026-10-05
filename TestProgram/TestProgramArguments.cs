using MFTLib;

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
    UsnWatch,

    /// <summary>Finds records by name, with the match flags the options select.</summary>
    FindName,

    /// <summary>Streams a parse as a native result, reporting progress, counters and retained records.</summary>
    StreamRecords,

    /// <summary>Parses a saved MFT image from a file, with no volume and no elevation.</summary>
    ParseFile,

    /// <summary>Reports the volume's NTFS MFT sizing.</summary>
    VolumeInfo,

    /// <summary>Grows the USN journal to sizes given on the command line.</summary>
    UsnGrow
}

/// <summary>
///     The parsed command line: an optional mode name, then drive letters (an MFT file path for
///     parse-file), then mode options. A first argument that names no mode is a drive letter and
///     selects <see cref="ProgramMode.FindGit" />.
/// </summary>
internal sealed partial record TestProgramArguments(ProgramMode Mode, IReadOnlyList<string> Drives, int WatchSeconds)
{
    internal const string DefaultDrive = "G";
    internal const int DefaultWatchSeconds = 10;
    internal const string SecondsOption = "--seconds";

    /// <summary>The other mode options; each is rejected on a mode it does not apply to.</summary>
    internal ModeOptions Options { get; init; } = new();

    internal static string Usage =>
        "Usage: TestProgram [mode] [drive ...] [options]" + Environment.NewLine +
        "  modes: " + string.Join(", ", ProgramModes.Names.Keys) + " (default find-git)" + Environment.NewLine +
        $"  drive defaults to {DefaultDrive}; --seconds applies to usn-watch (default {DefaultWatchSeconds})" +
        Environment.NewLine +
        "  find-name <drive> --name TEXT [--contains] [--include-freed] [--no-paths] [--buffer-size N]" +
        Environment.NewLine +
        "  stream-records <drive> [--name TEXT] [--contains] [--include-freed] [--no-paths] [--threads N]" +
        Environment.NewLine +
        "                 [--timeout-seconds N] [--buffer-size N] [--batch-size N]" + Environment.NewLine +
        "  parse-file <mft-file> [--name TEXT] [--contains] [--include-freed] [--no-paths] [--stream]" +
        Environment.NewLine +
        "             [--buffer-size N] [--batch-size N]  (needs no elevation)" + Environment.NewLine +
        "  read-records <drive> [--no-paths] [--timings] [--buffer-size N]" + Environment.NewLine +
        "  find-git and usn-read accept --buffer-size N" + Environment.NewLine +
        "  usn-grow <drive> --maximum-size BYTES --allocation-delta BYTES  (changes the volume; never shrinks)";

    /// <summary>
    ///     Scanning through the broker runs unelevated because the broker is the elevated process and
    ///     asks for elevation itself; parsing a saved MFT image touches no volume.
    /// </summary>
    internal bool RequiresElevation => Mode is not (ProgramMode.ScanDrive or ProgramMode.ParseFile);

    /// <summary>
    ///     The wait for the elevated copy: the completion allowance plus every drive's requested
    ///     watch or streaming time, or unbounded when streaming has no requested timeout.
    /// </summary>
    internal TimeSpan ElevationTimeout
    {
        get
        {
            long requestedSecondsPerDrive;
            if (Mode == ProgramMode.UsnWatch)
            {
                requestedSecondsPerDrive = WatchSeconds;
            }
            else if (Mode == ProgramMode.StreamRecords)
            {
                if (Options.TimeoutSeconds is not { } streamTimeout)
                {
                    // stream-records without --timeout-seconds lets the scan run, so the wait must not cap it.
                    return Timeout.InfiniteTimeSpan;
                }

                requestedSecondsPerDrive = streamTimeout;
            }
            else
            {
                requestedSecondsPerDrive = 0;
            }

            var requestedMilliseconds = Drives.Count * requestedSecondsPerDrive * 1000;
            var totalMilliseconds = ElevationUtilities.DefaultElevatedTimeout.TotalMilliseconds + requestedMilliseconds;
            return TimeSpan.FromMilliseconds(Math.Min(int.MaxValue, totalMilliseconds));
        }
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
        ["usn-watch"] = ProgramMode.UsnWatch,
        ["find-name"] = ProgramMode.FindName,
        ["stream-records"] = ProgramMode.StreamRecords,
        ["parse-file"] = ProgramMode.ParseFile,
        ["volume-info"] = ProgramMode.VolumeInfo,
        ["usn-grow"] = ProgramMode.UsnGrow
    };

    internal static string NameOf(ProgramMode mode)
    {
        return Names.First(entry => entry.Value == mode).Key;
    }
}
