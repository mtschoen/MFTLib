using MFTLib.Index;

namespace MFTLib;

public static partial class MftIndexSources
{
    /// <summary>
    ///     A source that loads an MFT dump file (a saved copy of a volume's <c>$MFT</c>) as one
    ///     virtual drive. It needs no elevation and no broker, and it works on every platform.
    ///     Open the index with exactly one drive, <c>new IndexedDrive(driveLetter, "dump:/" + driveLetter, 0)</c>,
    ///     with <see cref="FileIndexOptions.NoCache" /> set and <see cref="ProducerPolicy.Mft" />.
    ///     The dump is scanned in full. A dump drive cannot be watched, has no journal settings, and its
    ///     entries cannot be opened: the letter is a key in the index and names no live volume.
    /// </summary>
    /// <param name="filePath">
    ///     The dump file. The path is made absolute once, here, and the file is not opened: a missing,
    ///     unreadable or invalid file fails the scan, which the index reports as
    ///     <see cref="DriveFailureKind.ProducerFailed" /> with the reason in
    ///     <see cref="DriveStatus.FailureMessage" />. Each scan and rescan opens the path
    ///     anew and reads only the file it opened, so the file must not be rewritten in place during a scan.
    /// </param>
    /// <param name="driveLetter">The letter the index files the dump under; any ASCII letter, taken in upper case.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath" /> is null, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="driveLetter" /> is not an ASCII letter.</exception>
    public static MftIndexSource FromMftDumpFile(string filePath, char driveLetter)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A dump file path is required.", nameof(filePath));
        }

        if (!char.IsAsciiLetter(driveLetter))
        {
            throw new ArgumentOutOfRangeException(nameof(driveLetter), driveLetter,
                "The logical drive key must be an ASCII letter.");
        }

        var identity = new MftDumpSourceIdentity(filePath, driveLetter);
        var producer = new MftDumpBlockProducer(identity.DumpFilePath);
        return new MftIndexSource(producer.ProduceAsync, dumpIdentity: identity);
    }
}
