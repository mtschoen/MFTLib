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

    // Whether a run must be elevated itself. Scanning through the broker never must, because the broker is the
    // elevated process; the seam keeps the self-elevation path testable.
    internal Func<TestProgramArguments, bool> _requiresElevation = parsed => parsed.RequiresElevation;
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

        if (!_requiresElevation(parsed))
        {
            // scan-drive needs no elevation here, but the broker it launches raises a UAC prompt, so an attended
            // run gets the same heads-up dialog the self-elevation path gets before that prompt.
            if (!_isElevated())
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
            if (_canSelfElevate() && ConfirmElevation(arguments, SelfElevationReason) && _tryRunElevated(arguments, ElevationUtilities.DefaultElevatedTimeout))
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
        // The console entry point has no synchronization context, so blocking here cannot deadlock.
        ScanDrivesThroughBrokerAsync(parsed.Drives, CancellationToken.None).GetAwaiter().GetResult();
        _writeLine($"Completed at {DateTime.Now}");
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
