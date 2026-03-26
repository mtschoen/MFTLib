namespace MFTLib;

public static class MFTUtilities
{
    public static string GetFileNameForDriveLetter(string driveLetter)
    {
        if (driveLetter.EndsWith(':'))
            throw new ArgumentException("Drive letter should not end with a colon", nameof(driveLetter));

        if (driveLetter.Length != 1)
            throw new ArgumentException("Drive letter must be a single character", nameof(driveLetter));

        return @$"\\.\{driveLetter}:";
    }

    public static string GetVolumePath(string input)
    {
        if (string.IsNullOrEmpty(input)) throw new ArgumentNullException(nameof(input));

        // Volume GUID format: \\?\Volume{guid}\
        if (input.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            return input.EndsWith(@"\") ? input[..^1] : input;
        }

        // Drive letter format: C or C: or C:\
        var letter = input.TrimEnd('\\').TrimEnd(':');
        if (letter.Length == 1 && char.IsLetter(letter[0]))
        {
            return @$"\\.\{letter}:";
        }

        // Raw path format: \\.\C:
        if (input.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

        throw new ArgumentException($"Unrecognized volume format: {input}", nameof(input));
    }
}
