using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// The volume-level modes: MFT sizing, and the one mode that changes a volume. usn-grow never
// runs as part of another mode, and argument validation requires its sizes and one explicit drive.
partial class DriveScanner
{
    internal Func<string, NtfsVolumeInformation> _queryVolumeInformation = QueryVolumeInformationNative;

    internal Func<MftVolume, long, long, UsnJournalSettings> _growJournal =
        (volume, maximumSize, allocationDelta) => volume.GrowUsnJournal(maximumSize, allocationDelta);

    static NtfsVolumeInformation QueryVolumeInformationNative(string letter)
    {
        return OperatingSystem.IsWindows()
            ? NtfsVolumeInformation.Query(letter)
            : throw new PlatformNotSupportedException("Volume information is read on Windows only.");
    }

    internal void QueryVolumeInformation(string drive)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            var information = _queryVolumeInformation(letter);
            _writeLine($"MFT valid data length {information.MftValidDataLength} bytes");
            _writeLine($"Bytes per file record segment {information.BytesPerFileRecordSegment}");
            _writeLine($"Approximate MFT record count {information.MftRecordCount}");
            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }

    // NTFS never shrinks a journal, so the sizes are whatever the command line said and the
    // volume's settings are printed on both sides of the change.
    internal void GrowJournal(string drive, ModeOptions options)
    {
        RunOnVolume(drive, options, volume =>
        {
            var maximumSize = options.RequiredMaximumSize;
            var allocationDelta = options.RequiredAllocationDelta;
            _writeLine($"Before: {FormatSettings(_queryJournalSettings(volume))}");
            _writeLine($"Requesting maximum size {maximumSize} bytes, allocation delta {allocationDelta} bytes");
            var after = _growJournal(volume, maximumSize, allocationDelta);
            _writeLine($"After: {FormatSettings(after)}");
        });
    }
}
