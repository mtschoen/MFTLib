namespace MFTLib;

/// <summary>Normalizes a drive path ("C:\", "C:", "C", @"\\.\C:") to its bare upper-case letter ("C").</summary>
internal static class BrokerDriveLetter
{
    internal static string Normalize(string drive)
    {
        return TryNormalize(drive, out var normalizedDrive)
            ? normalizedDrive
            : throw new ArgumentException($"'{drive}' is not a valid drive letter.", nameof(drive));
    }

    internal static bool TryNormalize(string drive, out string normalizedDrive)
    {
        ArgumentNullException.ThrowIfNull(drive);

        var span = drive.AsSpan().Trim();
        if (span.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            span = span[4..];
        }

        while (span.Length > 0 && (span[^1] == ':' || span[^1] == '\\' || span[^1] == '/'))
        {
            span = span[..^1];
        }

        if (span.Length != 1 || !char.IsAsciiLetter(span[0]))
        {
            normalizedDrive = string.Empty;
            return false;
        }

        normalizedDrive = char.ToUpperInvariant(span[0]).ToString();
        return true;
    }
}
