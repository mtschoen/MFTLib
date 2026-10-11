using System.Runtime.InteropServices;
using MFTLib;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// What a verb needs from the process before it runs: nothing, the elevated broker (which raises the UAC prompt
// itself), or this process elevated because the verb reads a volume directly.
internal enum ElevationNeed { None, BrokerLaunch, SelfElevate }

// The elevation flow both samples share: the heads-up dialog, the self-elevating relaunch, the unattended skip and
// the output.log redirect of an elevated run. Each sample compiles this file as linked source.
partial class SampleHost
{
    internal Func<uint, IntPtr> _acrtIobFunc = AcrtIobFuncNative;
    // The provider is the library's injectable face of the same three elevation calls.
    static readonly IElevationProvider Elevation = ElevationUtilities.DefaultProvider;

    internal Func<bool> _canSelfElevate = Elevation.CanSelfElevate;
    internal Func<string?> _getProcessPath = () => Environment.ProcessPath;
    internal Func<bool> _isElevated = Elevation.IsElevated;
    internal Func<IReadOnlyList<string>, TimeSpan, bool> _tryRunElevated = Elevation.TryRunElevated;
    internal Func<string, string, IntPtr, IntPtr> _wFreopen = WFreopenNative;
    internal Action<string> _writeLine = Console.WriteLine;

    /// <summary>
    ///     Runs a verb once the process has what <paramref name="need" /> asks for. Returns 1 when elevation was
    ///     required and could not be had, the verb's own code once it ran, and 0 once the elevated relaunch took over.
    /// </summary>
    internal int RunWithElevation(string[] arguments, ElevationNeed need, Func<int> run)
    {
        if (need is ElevationNeed.None)
        {
            return run();
        }

        if (_isElevated())
        {
            // An elevated self-elevating run is the relaunched child, so its output goes to a file.
            if (need is ElevationNeed.SelfElevate)
            {
                RedirectStdout(OutputLogPath);
            }

            return run();
        }

        if (IsUnattended())
        {
            return SkipElevationUnattended(arguments);
        }

        if (need is ElevationNeed.BrokerLaunch)
        {
            // The broker raises a UAC prompt, so an attended run gets the same heads-up dialog the
            // self-elevation path gets before that prompt.
            if (!ConfirmElevation(arguments, BrokerLaunchReason))
            {
                PrintElevationFailure(arguments);
                return 1;
            }

            return run();
        }

        _writeLine("Not running as administrator. Attempting to self-elevate...");
        if (_canSelfElevate() && ConfirmElevation(arguments, SelfElevationReason))
        {
            // Announced before the attempt: a failed elevated run leaves its error only in that file.
            _writeLine($"The elevated run writes its output to {OutputLogPath}");
            if (_tryRunElevated(arguments, Timeout.InfiniteTimeSpan))
            {
                return 0;
            }

            PrintElevatedRunIncomplete();
            return 1;
        }

        PrintElevationFailure(arguments);
        return 1;
    }

    // A false result from the confirmed relaunch covers both "no elevated child ran" (the launch
    // failed, or the UAC prompt was cancelled) and "the child ran but did not finish successfully"
    // (a nonzero exit). The notice says the run did not complete successfully and names the announced
    // log as where its output lies if a run started.
    void PrintElevatedRunIncomplete()
    {
        _writeLine("------------------------------------------------------------------");
        _writeLine("THE ELEVATED RUN DID NOT COMPLETE SUCCESSFULLY.");
        _writeLine($"If the elevated run started, its output is in {OutputLogPath}");
        _writeLine("------------------------------------------------------------------");
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

    // The elevated child's window closes when it ends, so its output goes to this file beside the executable.
    static string OutputLogPath => Path.Combine(AppContext.BaseDirectory, "output.log");

    void RedirectStdout(string logPath)
    {
        _outputRedirectedToFile = true;
        var stdout = _acrtIobFunc(1);
        _wFreopen(logPath, "w", stdout);
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_wfreopen", CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr WFreopenNative(string path, string mode, IntPtr stream);

    [DllImport("ucrtbase.dll", EntryPoint = "__acrt_iob_func", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr AcrtIobFuncNative(uint index);
}
