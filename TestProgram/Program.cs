using System.Runtime.InteropServices;
using MFTLib;

// Redirect C runtime stdout to a log file. This captures both native
// printf output from the DLL and managed Console.WriteLine, so we can
// read results after the elevated process exits.
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
        var volume = MFTUtilities.GetFileNameForDriveLetter(letter);
        var volumeHandle = FileUtilities.GetVolumeHandle(volume);
        MFTParse.DumpVolumeInfo(volumeHandle);
        MFTParse.ParseMFT(volumeHandle);
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
