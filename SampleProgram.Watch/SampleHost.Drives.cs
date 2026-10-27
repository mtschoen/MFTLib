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

        foreach (var volume in IndexedRoot.EnumerateWindowsVolumes())
        {
            yield return volume;
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

        var volumes = availableDrives as IReadOnlyList<WindowsVolume>
            ?? availableDrives.ToList();
        foreach (var volume in volumes.Where(volume => !volume.IsIndexable))
        {
            _output.WriteLine($"Skipping drive {volume.RootDirectory} ({volume.DriveType}, {(volume.IsReady ? "ready" : "not ready")}, format {volume.Format ?? "unknown"}: {volume.Reason})");
        }

        var chosen = volumes.Where(volume => volume.IsIndexable)
            .Select(volume => volume.Volume.ToString())
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
