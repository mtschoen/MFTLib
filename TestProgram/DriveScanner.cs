using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib;

namespace TestProgram;

class DriveScanner
{
    internal Func<uint, IntPtr> _acrtIobFunc = AcrtIobFuncNative;
    internal Func<bool> _canSelfElevate = ElevationUtilities.CanSelfElevate;
    internal Func<string?> _getProcessPath = ElevationUtilities.GetProcessPath;
    internal Func<bool> _isElevated = ElevationUtilities.IsElevated;
    internal Func<string, MftVolume> _openVolume = letter => MftVolume.Open(letter);
    internal Func<string, bool> _tryRunElevated = arguments => ElevationUtilities.TryRunElevated(arguments);
    internal Func<string, string, IntPtr, IntPtr> _wFreopen = WFreopenNative;
    internal Action<string> _writeLine = Console.WriteLine;

    internal static string FormatArguments(string[] arguments)
    {
        return string.Join(" ", arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
    }

    internal int Run(string[] arguments)
    {
        if (!_isElevated())
        {
            var formattedArguments = FormatArguments(arguments);
            _writeLine("Not running as administrator. Attempting to self-elevate...");
            if (_canSelfElevate() && _tryRunElevated(formattedArguments))
            {
                return 0;
            }

            PrintElevationFailure(arguments);
            return 1;
        }

        var logPath = Path.Combine(AppContext.BaseDirectory, "output.log");
        RedirectStdout(logPath);

        var driveLetters = arguments.Length > 0 ? arguments : ["G"];

        foreach (var drive in driveLetters)
        {
            ScanDrive(drive);
        }

        _writeLine($"Completed at {DateTime.Now}");
        return 0;
    }

    internal void ScanDrive(string drive)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            using var volume = _openVolume(letter);

            var stopwatch = Stopwatch.StartNew();
            var records = volume.FindByName(".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths, out var timings);
            stopwatch.Stop();

            var gitDirectories = records.Where(record => record.IsDirectory).ToArray();

            _writeLine($"Found {gitDirectories.Length} .git directories in {stopwatch.Elapsed}");
            _writeLine(string.Empty);
            _writeLine("Performance breakdown:");
            _writeLine($"  {timings}");
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

    void PrintElevationFailure(string[] arguments)
    {
        var formattedArguments = FormatArguments(arguments);
        _writeLine("------------------------------------------------------------------");
        _writeLine("AUTOMATIC ELEVATION FAILED.");
        _writeLine("This program requires Administrative privileges to read the MFT.");
        _writeLine("Please run this command from an ELEVATED terminal:");
        _writeLine(string.Empty);
        _writeLine($"  {_getProcessPath()} {formattedArguments}");
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
