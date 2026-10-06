using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib;

namespace TestProgram;

partial class DriveScanner
{
    internal Func<uint, IntPtr> _acrtIobFunc = AcrtIobFuncNative;
    // The provider is the library's injectable face of the same three elevation calls.
    static readonly IElevationProvider Elevation = ElevationUtilities.DefaultProvider;

    internal Func<bool> _canSelfElevate = Elevation.CanSelfElevate;
    internal Func<string?> _getProcessPath = () => Environment.ProcessPath;
    internal Func<bool> _isElevated = Elevation.IsElevated;
    internal Func<string, MftVolume> _openVolume = letter => MftVolume.Open(letter);
    internal Func<string, uint, MftVolume> _openVolumeWithBuffer = MftVolume.Open;

    internal Func<IReadOnlyList<string>, TimeSpan, bool> _tryRunElevated = Elevation.TryRunElevated;
    internal Func<string, string, IntPtr, IntPtr> _wFreopen = WFreopenNative;
    internal Action<string> _writeLine = Console.WriteLine;

    internal int Run(string[] arguments)
    {
        if (!TestProgramArguments.TryParse(arguments, out var parsed, out var error))
        {
            _writeLine(error);
            _writeLine(TestProgramArguments.Usage);
            return 2;
        }

        if (parsed.Index is { } indexVerb)
        {
            return RunIndexVerb(indexVerb, arguments);
        }

        if (!parsed.RequiresElevation)
        {
            // scan-drive needs no elevation here, but the broker it launches raises a UAC prompt, so an attended
            // run gets the same heads-up dialog the self-elevation path gets before that prompt.
            if (parsed.Mode == ProgramMode.ScanDrive && !_isElevated())
            {
                if (IsUnattended())
                {
                    return SkipElevationUnattended(arguments);
                }

                if (!ConfirmElevation(arguments, BrokerLaunchReason))
                {
                    PrintElevationFailure(arguments);
                    return 1;
                }
            }

            RunOnDrives(parsed);
            return 0;
        }

        if (!_isElevated())
        {
            if (IsUnattended())
            {
                return SkipElevationUnattended(arguments);
            }

            _writeLine("Not running as administrator. Attempting to self-elevate...");
            if (_canSelfElevate() && ConfirmElevation(arguments, SelfElevationReason) && _tryRunElevated(arguments, parsed.ElevationTimeout))
            {
                return 0;
            }

            PrintElevationFailure(arguments);
            return 1;
        }

        var logPath = Path.Combine(AppContext.BaseDirectory, "output.log");
        RedirectStdout(logPath);

        RunOnDrives(parsed);
        return 0;
    }

    void RunOnDrives(TestProgramArguments parsed)
    {
        if (parsed.Mode == ProgramMode.ScanDrive)
        {
            // The console entry point has no synchronization context, so blocking here cannot deadlock.
            ScanDrivesThroughBrokerAsync(parsed.Drives, CancellationToken.None).GetAwaiter().GetResult();
            _writeLine($"Completed at {DateTime.Now}");
            return;
        }

        if (parsed.Mode == ProgramMode.ParseFile)
        {
            ParseFile(parsed.Options);
            _writeLine($"Completed at {DateTime.Now}");
            return;
        }

        foreach (var drive in parsed.Drives)
        {
            switch (parsed.Mode)
            {
                case ProgramMode.ReadRecords:
                    ReadRecords(drive, parsed.Options);
                    break;
                case ProgramMode.FindName:
                    FindName(drive, parsed.Options);
                    break;
                case ProgramMode.StreamRecords:
                    StreamRecords(drive, parsed.Options);
                    break;
                case ProgramMode.VolumeInfo:
                    QueryVolumeInformation(drive);
                    break;
                case ProgramMode.UsnGrow:
                    GrowJournal(drive, parsed.Options);
                    break;
                case ProgramMode.UsnQuery:
                    QueryJournal(drive);
                    break;
                case ProgramMode.UsnRead:
                    ReadJournal(drive, parsed.Options);
                    break;
                case ProgramMode.UsnWatch:
                    // The console entry point has no synchronization context, so blocking here cannot deadlock.
                    WatchJournalAsync(drive, parsed.WatchSeconds).GetAwaiter().GetResult();
                    break;
                default:
                    ScanDrive(drive, parsed.Options);
                    break;
            }
        }

        _writeLine($"Completed at {DateTime.Now}");
    }

    internal void ScanDrive(string drive, ModeOptions options)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            using var volume = OpenVolume(letter, options);

            var stopwatch = Stopwatch.StartNew();
            using var result = volume.StreamRecords(
                ".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths, null, null, CancellationToken.None);
            var records = result.ToArray();
            stopwatch.Stop();

            var gitDirectories = records.Where(record => record.IsDirectory).ToArray();

            _writeLine($"Found {gitDirectories.Length} .git directories in {stopwatch.Elapsed}");
            _writeLine(string.Empty);
            _writeLine("Performance breakdown:");
            _writeLine($"  {result.Timings}");
            _writeLine($"  Wall clock: {stopwatch.Elapsed.TotalMilliseconds:F1}ms");
            _writeLine($"  Matched {records.Length} records (marshalled), {gitDirectories.Length} directories");
            _writeLine(string.Empty);

            foreach (var directory in gitDirectories)
            {
                _writeLine($"  {directory.FullPath}");
            }

            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }

    /// <summary>
    ///     Prints the elevation failure notice. The arguments are listed as data, one per line and verbatim, never
    ///     as a command line: no quoting rule is correct for every shell, so the user re-enters them in their own.
    ///     An empty argument is shown as <c>&lt;empty&gt;</c>.
    /// </summary>
    void PrintElevationFailure(string[] arguments)
    {
        _writeLine("------------------------------------------------------------------");
        _writeLine("AUTOMATIC ELEVATION FAILED.");
        _writeLine("This program requires Administrative privileges to read the MFT.");
        _writeLine($"Run this program from an ELEVATED terminal: {_getProcessPath()}");
        _writeLine($"Arguments ({arguments.Length}), one per line exactly as received; <empty> marks an empty argument:");
        foreach (var line in ArgumentLines(arguments))
        {
            _writeLine(line);
        }

        _writeLine("------------------------------------------------------------------");
    }

    void RedirectStdout(string logPath)
    {
        var stdout = _acrtIobFunc(1);
        _wFreopen(logPath, "w", stdout);
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_wfreopen", CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr WFreopenNative(string path, string mode, IntPtr stream);

    [DllImport("ucrtbase.dll", EntryPoint = "__acrt_iob_func", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr AcrtIobFuncNative(uint index);
}
