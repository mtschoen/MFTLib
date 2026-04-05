using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib;

namespace TestProgram;

internal class DriveScanner
{
    internal Func<bool> IsElevated = ElevationUtilities.IsElevated;
    internal Func<bool> CanSelfElevate = ElevationUtilities.CanSelfElevate;
    internal Func<string, bool> TryRunElevated = arguments => ElevationUtilities.TryRunElevated(arguments);
    internal Func<string?> GetProcessPath = ElevationUtilities.GetProcessPath;
    internal Func<uint, IntPtr> AcrtIobFunc = AcrtIobFuncNative;
    internal Func<string, string, IntPtr, IntPtr> WFreopen = WFreopenNative;
    internal Func<string, MftVolume> OpenVolume = letter => MftVolume.Open(letter);
    internal Action<string> WriteLine = Console.WriteLine;
    internal Action<string> Write = Console.Write;

    internal static string FormatArguments(string[] arguments)
    {
        return string.Join(" ", arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
    }

    internal int Run(string[] arguments)
    {
        if (!IsElevated())
        {
            var formattedArguments = FormatArguments(arguments);
            WriteLine("Not running as administrator. Attempting to self-elevate...");
            if (CanSelfElevate() && TryRunElevated(formattedArguments))
                return 0;

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

        WriteLine($"Completed at {DateTime.Now}");
        return 0;
    }

    internal void ScanDrive(string drive)
    {
        var letter = drive.TrimEnd(':');
        WriteLine($"=== Drive {letter}: ===");
        try
        {
            using var volume = OpenVolume(letter);

            var stopwatch = Stopwatch.StartNew();
            var records = volume.FindByName(".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths, out var timings);
            stopwatch.Stop();

            var gitDirectories = records.Where(record => record.IsDirectory).ToArray();

            WriteLine($"Found {gitDirectories.Length} .git directories in {stopwatch.Elapsed}");
            WriteLine(string.Empty);
            WriteLine("Performance breakdown:");
            WriteLine($"  {timings}");
            WriteLine($"  Wall clock: {stopwatch.Elapsed.TotalMilliseconds:F1}ms");
            WriteLine($"  Matched {records.Length} records (marshalled), {gitDirectories.Length} directories");
            WriteLine(string.Empty);

            foreach (var directory in gitDirectories)
            {
                WriteLine($"  {directory.FullPath}");
            }

            WriteLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            WriteLine($"Error on drive {letter}: {exception.Message}");
        }

        WriteLine(string.Empty);
    }

    void PrintElevationFailure(string[] arguments)
    {
        var formattedArguments = FormatArguments(arguments);
        WriteLine("------------------------------------------------------------------");
        WriteLine("AUTOMATIC ELEVATION FAILED.");
        WriteLine("This program requires Administrative privileges to read the MFT.");
        WriteLine("Please run this command from an ELEVATED terminal:");
        WriteLine(string.Empty);
        WriteLine($"  {GetProcessPath()} {formattedArguments}");
        WriteLine("------------------------------------------------------------------");
    }

    void RedirectStdout(string logPath)
    {
        var stdout = AcrtIobFunc(1);
        WFreopen(logPath, "w", stdout);
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_wfreopen", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr WFreopenNative(string path, string mode, IntPtr stream);

    [DllImport("ucrtbase.dll", EntryPoint = "__acrt_iob_func", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr AcrtIobFuncNative(uint index);
}
