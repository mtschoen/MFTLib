using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

internal static class JournalWindows
{
    public static SyntheticJournalWindow Window(ulong journalIdentifier, long firstUsn, long nextUsn,
        long allocationDelta = 64, long maximumSize = 128L * 1024 * 1024) =>
        new(journalIdentifier, firstUsn, nextUsn, allocationDelta, maximumSize);

    public static IDisposable Journal(ulong journalIdentifier = 0xABCD, long firstUsn = 1_000,
        long nextUsn = 5_000, long maximumSize = 128L * 1024 * 1024, long allocationDelta = 64) =>
        Override(_ => Window(journalIdentifier, firstUsn, nextUsn, allocationDelta, maximumSize));

    public static IDisposable Override(Func<DriveKey, SyntheticJournalWindow?> query) =>
        JournalIsolation.OverrideJournalWindow(query);

    public static IDisposable ForDrives(IReadOnlyDictionary<DriveKey, SyntheticJournalWindow> windows) =>
        Override(drive => windows.TryGetValue(drive, out var window) ? window : null);
}
