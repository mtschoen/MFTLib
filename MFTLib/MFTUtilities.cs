namespace MFTLib;

public static class MFTUtilities
{
    public static string GetFileNameForDriveLetter(string driveLetter)
    {
        if (driveLetter.EndsWith(':'))
            throw new ArgumentException("Drive letter should not end with a colon", nameof(driveLetter));

        return @$"\\.\{driveLetter}:";
    }
}