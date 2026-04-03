using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib;

// Self-elevation logic for TestProgram
if (!ElevationUtilities.IsElevated())
{
    Console.WriteLine("Not running as administrator. Attempting to self-elevate...");
    var formattedArgs = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    if (ElevationUtilities.CanSelfElevate() && ElevationUtilities.TryRunElevated(formattedArgs))
        return;

    Console.WriteLine("------------------------------------------------------------------");
    Console.WriteLine("AUTOMATIC ELEVATION FAILED.");
    Console.WriteLine("This program requires Administrative privileges to read the MFT.");
    Console.WriteLine("Please run this command from an ELEVATED terminal:");
    Console.WriteLine();
    Console.WriteLine($"  {ElevationUtilities.GetProcessPath()} {formattedArgs}");
    Console.WriteLine("------------------------------------------------------------------");
    return;
}

// Redirect C runtime stdout to a log file ONLY when elevated. 
// This captures both native printf output from the DLL and managed Console.WriteLine.
var logPath = Path.Combine(AppContext.BaseDirectory, "output.log");
var stdout = __acrt_iob_func(1); // FILE* for stdout
_wfreopen(logPath, "w", stdout);

var driveLetters = args.Length > 0 ? args : ["G"];

foreach (var drive in driveLetters)
{
    var letter = drive.TrimEnd(':');
    Console.WriteLine($"=== Drive {letter}: ===");
    try
    {
        using var volume = MftVolume.Open(letter);

        var sw = Stopwatch.StartNew();
        var records = volume.FindByName(".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths, out var timings);
        sw.Stop();

        var gitDirs = records.Where(r => r.IsDirectory).ToArray();

        Console.WriteLine($"Found {gitDirs.Length} .git directories in {sw.Elapsed}");
        Console.WriteLine();
        Console.WriteLine("Performance breakdown:");
        Console.WriteLine($"  {timings}");
        Console.WriteLine($"  Wall clock: {sw.Elapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"  Matched {records.Length} records (marshalled), {gitDirs.Length} directories");
        Console.WriteLine();

        foreach (var dir in gitDirs)
        {
            Console.WriteLine($"  {dir.FullPath}");
        }

        Console.WriteLine($"=== Drive {letter}: done ===");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error on drive {letter}: {ex.Message}");
    }
    Console.WriteLine();
}

Console.WriteLine($"Completed at {DateTime.Now}");

[DllImport("ucrtbase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr _wfreopen(string path, string mode, IntPtr stream);

[DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr __acrt_iob_func(uint index);
