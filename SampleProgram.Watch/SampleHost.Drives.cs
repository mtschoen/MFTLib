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

        foreach (var volume in WindowsVolume.EnumerateAll())
        {
            yield return volume;
        }
    }

    // The volumes the host knows; tests replace it with synthetic ones.
    internal Func<IEnumerable<WindowsVolume>> _volumes = EnumerateDrives;

    internal bool TryChooseDrives(
        ref WatchArguments parsed,
        IEnumerable<WindowsVolume> availableDrives)
    {
        if (!parsed.NeedsRoots || parsed.Roots.Count > 0)
        {
            return true;
        }

        var volumes = availableDrives as IReadOnlyList<WindowsVolume>
            ?? availableDrives.ToList();
        foreach (var volume in volumes.Where(volume => !volume.IsIndexable))
        {
            _output.WriteLine($"Skipping drive {volume.RootDirectory} ({volume.DriveType}, {(volume.IsReady ? "ready" : "not ready")}, format {volume.Format ?? "unknown"}: {volume.Reason})");
        }

        var indexable = volumes.Where(volume => volume.IsIndexable)
            .OrderBy(volume => volume.Volume.ToString(), StringComparer.Ordinal).ToArray();
        var chosen = indexable.Select(volume => (RootKey)volume.Volume).ToArray();
        if (chosen.Length == 0)
        {
            _output.WriteLine("No root was named and no ready local NTFS drive was found. Name the roots to use, for example: C D");
            return false;
        }

        _output.WriteLine($"No root was named, so this run covers every ready local NTFS drive: {string.Join(", ", indexable.Select(volume => volume.Volume.ToString()))}. Name roots to choose others.");
        parsed = parsed with { Roots = chosen };
        return true;
    }
}
