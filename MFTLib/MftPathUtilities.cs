namespace MFTLib;

public static class MftPathUtilities
{
    public static string ResolvePath(ulong recordNumber, IReadOnlyDictionary<ulong, MftRecord> lookup, string driveLetter)
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
}
