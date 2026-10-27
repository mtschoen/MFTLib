using MFTLib.Index;

namespace MFTLib;

public static partial class MftIndexSources
{
    /// <summary>
    ///     A source that loads an MFT dump file (a saved copy of a volume's <c>$MFT</c>) as one
    ///     virtual volume. It needs no elevation and no broker, and it works on every platform.
    ///     Supply the result through <see cref="FileIndexOptions.MftSource" />. The source supplies its
    ///     one virtual volume (the normalized upper-case <paramref name="volume" />,
    ///     root <c>"dump:/" + letter</c>, serial zero, MFT producer), which
    ///     <see cref="FileIndex.Status" /> and <see cref="FileIndex.Root" /> then report.
    ///     <see cref="FileIndexOptions.Root" /> must be null;
    ///     <see cref="FileIndexOptions.CacheMode" /> and <see cref="FileIndexOptions.CacheDirectory" />
    ///     must be null, and <see cref="FileIndexOptions.CacheTag" /> must be default.
    ///     Other values make <see cref="FileIndex.OpenAsync" /> throw <see cref="ArgumentException" />.
    ///     Every non-null cache mode, including <see cref="CacheMode.Disabled" />, is rejected.
    ///     The dump is scanned in full. A dump volume cannot be watched, has no journal settings, and its
    ///     entries cannot be opened: the letter is a key in the index and names no live volume.
    /// </summary>
    /// <param name="filePath">
    ///     The dump file. The path is made absolute once, here, and the file is not opened: a missing,
    ///     unreadable or invalid file fails the scan, which the index reports as
    ///     <see cref="RootFailureKind.ProducerFailed" /> with the reason in
    ///     <see cref="RootFailure.Message" />. Each scan and rescan opens the path
    ///     anew and reads only the file it opened, so the file must not be rewritten in place during a scan.
    /// </param>
    /// <param name="volume">The logical volume key the index files the dump under.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath" /> is null, empty or white space.</exception>
    /// <exception cref="ArgumentException"><paramref name="volume" /> is the default key.</exception>
    public static MftIndexSource FromMftDumpFile(string filePath, VolumeKey volume)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A dump file path is required.", nameof(filePath));
        }

        var identity = new MftDumpSourceIdentity(filePath, volume);
        var producer = new MftDumpBlockProducer(identity.DumpFilePath, TimeProvider.System);
        return new MftIndexSource(producer.ProduceAsync, dumpIdentity: identity);
    }
}
