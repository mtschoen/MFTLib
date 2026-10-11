namespace SampleProgram.Watch;

// The drives a run covers when none is named: every ready NTFS fixed drive of the machine.
partial class SampleHost
{
    internal readonly record struct DriveFacts(string Name, DriveType Type, bool IsReady, string? Format);

    internal Func<IReadOnlyList<DriveFacts>> _listDrives = ListDrivesNative;

    static IReadOnlyList<DriveFacts> ListDrivesNative()
    {
        return [.. DriveInfo.GetDrives().Select(FactsOf)];
    }

    // A drive that goes away between the listing and the read is not ready.
    static DriveFacts FactsOf(DriveInfo drive)
    {
        try
        {
            return drive.IsReady
                ? new DriveFacts(drive.Name, drive.DriveType, true, drive.DriveFormat)
                : new DriveFacts(drive.Name, drive.DriveType, false, null);
        }
        catch (IOException)
        {
            return new DriveFacts(drive.Name, drive.DriveType, false, null);
        }
    }

    internal static IReadOnlyList<string> ReadyNtfsFixedDrives(IEnumerable<DriveFacts> drives)
    {
        return
        [
            .. drives
                .Where(drive => drive is { Type: DriveType.Fixed, IsReady: true } && drive.Name.Length > 0
                                && char.IsAsciiLetter(drive.Name[0])
                                && string.Equals(drive.Format, "NTFS", StringComparison.OrdinalIgnoreCase))
                .Select(drive => char.ToUpperInvariant(drive.Name[0]).ToString())
                .Order(StringComparer.Ordinal)
        ];
    }

    // An index mode that names no drive covers the machine; the run says which drives and why.
    bool TryChooseDrives(ref WatchArguments parsed)
    {
        if (!parsed.NeedsDrives || parsed.Drives.Count > 0)
        {
            return true;
        }

        var chosen = ReadyNtfsFixedDrives(_listDrives());
        if (chosen.Count == 0)
        {
            _writeLine("No drive was named and no ready NTFS fixed drive was found. Name the drives to use, for example: C D");
            return false;
        }

        _writeLine($"No drive was named, so this run covers every ready NTFS fixed drive: {string.Join(", ", chosen)}. Name drives to choose others.");
        parsed = parsed with { Drives = chosen };
        return true;
    }
}
