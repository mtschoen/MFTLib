using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Reads the current sizing of the USN journal of this index's root volume without elevation. The
    ///     library reports the size that would have retained a lost checkpoint in JournalCheckpointLoss; the
    ///     consumer owns the decision to grow. Resizing is an explicit elevated broker operation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The root's source has no live journal settings, or the root is a walked folder rather than a volume.</exception>
    /// <exception cref="PlatformNotSupportedException">The host is not Windows.</exception>
    /// <exception cref="IOException">The volume's root directory could not be opened.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows rejects the journal query, for example because the journal is not active.</exception>
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification = "Windows-only pass-through to ReadUsnJournalSettings; the target is covered.")]
    public UsnJournalSettings QueryUsnJournalSettings() => ReadUsnJournalSettings();

    /// <summary>
    ///     The logic behind <see cref="QueryUsnJournalSettings" />: refuses a source without live journal
    ///     settings, and otherwise reads the root's volume, which throws
    ///     <see cref="PlatformNotSupportedException" /> off Windows.
    /// </summary>
    internal UsnJournalSettings ReadUsnJournalSettings()
    {
        ThrowIfDisposed();
        return _view.Scan.QueryUsnJournalSettings();
    }
}
