using System.Diagnostics;
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
        var sw = Stopwatch.StartNew();
        using var volume = MftVolume.Open(letter);
        var records = volume.ReadAllRecords(out var timings);
        sw.Stop();

        Console.WriteLine($"Read {records.Length} records in {sw.Elapsed}");

        var dirs = records.Count(r => r.IsDirectory);
        var files = records.Length - dirs;
        Console.WriteLine($"  Directories: {dirs}");
        Console.WriteLine($"  Files: {files}");

        Console.WriteLine();
        Console.WriteLine("Performance breakdown:");
        Console.WriteLine($"  {timings}");
        Console.WriteLine($"  Wall clock: {sw.Elapsed.TotalMilliseconds:F1}ms");

        // Find .git directories
        Console.WriteLine();
        Console.WriteLine("Looking for .git directories...");

        var lookup = new Dictionary<ulong, MftRecord>();
        foreach (var r in records)
            lookup[r.RecordNumber] = r;

        int gitCount = 0;
        foreach (var record in records)
        {
            if (record.IsDirectory && string.Equals(record.FileName, ".git", StringComparison.OrdinalIgnoreCase))
            {
                var path = ResolvePath(record.RecordNumber, lookup, letter);
                Console.WriteLine($"  {path}");
                gitCount++;
            }
        }
        Console.WriteLine($"Found {gitCount} .git directories.");

        Console.WriteLine($"=== Drive {letter}: done ===");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error on drive {letter}: {ex.Message}");
    }
    Console.WriteLine();
}

Console.WriteLine($"Completed at {DateTime.Now}");

static string ResolvePath(ulong recordNumber, Dictionary<ulong, MftRecord> lookup, string driveLetter)
{
    var parts = new List<string>();
    var current = recordNumber;
    var visited = new HashSet<ulong>();

    while (current != 5 && lookup.TryGetValue(current, out var record) && visited.Add(current))
    {
        parts.Add(record.FileName);
        current = record.ParentRecordNumber;
    }

    parts.Reverse();
    return $"{driveLetter}:\\{string.Join('\\', parts)}";
}

[DllImport("ucrtbase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr _wfreopen(string path, string mode, IntPtr stream);

[DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr __acrt_iob_func(uint index);
