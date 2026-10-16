using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    internal Func<IReadOnlyList<(string RootDirectory, bool IsIndexable, string? Reason)>> _listDrives =
        () => OperatingSystem.IsWindows() ? IndexedDrive.EnumerateWindowsVolumes() : [];

    bool TryChooseDrives(ref WatchArguments parsed)
    {
        if (!parsed.NeedsDrives || parsed.Drives.Count > 0)
        {
            return true;
        }

        var drives = _listDrives();
        foreach (var drive in drives.Where(drive => !drive.IsIndexable))
        {
            _writeLine($"Skipping drive {drive.RootDirectory} ({drive.Reason})");
        }
        var chosen = drives.Where(drive => drive.IsIndexable)
            .Select(drive => new DriveKey(drive.RootDirectory[0]).ToString())
            .Order(StringComparer.Ordinal).ToArray();
        if (chosen.Length == 0)
        {
            _writeLine("No drive was named and no ready NTFS fixed drive was found. Name the drives to use, for example: C D");
            return false;
        }

        _writeLine($"No drive was named, so this run covers every ready NTFS fixed drive: {string.Join(", ", chosen)}. Name drives to choose others.");
        parsed = parsed with { Drives = chosen };
        return true;
    }
}
