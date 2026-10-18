using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    static IEnumerable<WindowsVolume> EnumerateDrives()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var drive in IndexedDrive.EnumerateWindowsVolumes())
        {
            yield return drive;
        }
    }

    internal bool TryChooseDrives(
        ref WatchArguments parsed,
        IEnumerable<WindowsVolume> availableDrives)
    {
        if (!parsed.NeedsDrives || parsed.Drives.Count > 0)
        {
            return true;
        }

        var drives = availableDrives as IReadOnlyList<WindowsVolume>
            ?? availableDrives.ToList();
        foreach (var drive in drives.Where(drive => !drive.IsIndexable))
        {
            _output.WriteLine($"Skipping drive {drive.RootDirectory} ({drive.DriveType}, {(drive.IsReady ? "ready" : "not ready")}, format {drive.Format ?? "unknown"}: {drive.Reason})");
        }

        var chosen = drives.Where(drive => drive.IsIndexable)
            .Select(drive => drive.Drive.ToString())
            .Order(StringComparer.Ordinal).ToArray();
        if (chosen.Length == 0)
        {
            _output.WriteLine("No drive was named and no ready NTFS fixed drive was found. Name the drives to use, for example: C D");
            return false;
        }

        _output.WriteLine($"No drive was named, so this run covers every ready NTFS fixed drive: {string.Join(", ", chosen)}. Name drives to choose others.");
        parsed = parsed with { Drives = chosen };
        return true;
    }
}
