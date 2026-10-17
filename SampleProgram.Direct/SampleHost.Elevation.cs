using System.Runtime.InteropServices;

namespace SampleProgram.Direct;

internal enum ElevationNeed { None, SelfElevate }

partial class SampleHost
{
    const string SelfElevationReason = "SampleProgram relaunches itself elevated.";

    internal Func<uint, IntPtr> _acrtIobFunc = AcrtIobFuncNative;
    internal Func<IReadOnlyList<string>, TimeSpan, bool> _tryRunElevated = Elevation.TryRunElevated;
    internal Func<string, string, IntPtr, IntPtr> _wFreopen = WFreopenNative;

    // An elevated relaunch writes to output.log and its window closes at once; the unelevated parent holds the window.
    bool _outputRedirectedToFile;

    internal int RunWithElevation(string[] arguments, ElevationNeed need, Func<int> run)
    {
        if (need is ElevationNeed.None) return run();
        if (_isElevated())
        {
            RedirectStdout(OutputLogPath);
            return run();
        }
        if (IsUnattended()) return SkipElevationUnattended(arguments);

        _writeLine("Not running as administrator. Attempting to self-elevate...");
        if (_canSelfElevate() && ConfirmElevation(arguments, SelfElevationReason))
        {
            _writeLine($"The elevated run writes its output to {OutputLogPath}");
            if (_tryRunElevated(arguments, Timeout.InfiniteTimeSpan)) return 0;
            PrintElevatedRunIncomplete();
            return 1;
        }
        PrintElevationFailure(arguments);
        return 1;
    }

    void PrintElevatedRunIncomplete()
    {
        _writeLine("------------------------------------------------------------------");
        _writeLine("THE ELEVATED RUN DID NOT COMPLETE SUCCESSFULLY.");
        _writeLine($"If the elevated run started, its output is in {OutputLogPath}");
        _writeLine("------------------------------------------------------------------");
    }

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
