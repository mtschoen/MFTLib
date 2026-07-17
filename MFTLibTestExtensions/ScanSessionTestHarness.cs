using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
/// Test-only entry points for constructing a <see cref="JournalBrokerScanSession"/> over a
/// caller-supplied client factory instead of a real elevated broker. Consumers of MFTLib
/// reference this assembly from their own test projects to unit-test the arguments their
/// discovery layer passes into a session (drives, <see cref="BrokerScanProfile"/>,
/// keep-file names) without MFTLib having to friend-list the consumer's test assembly.
/// </summary>
/// <remarks>
/// Each method mirrors one internal session-start seam, differing from the shipping public
/// overloads only in that the caller injects the <see cref="JournalBrokerClient"/> factory
/// directly rather than going through the elevated <c>SpawnAndConnectAsync</c> path. Build
/// the client with the public <see cref="JournalBrokerClient"/> stream constructor over an
/// in-memory duplex stream; drive the broker side of that stream from the test to answer
/// the frames the session sends.
/// </remarks>
public static class ScanSessionTestHarness
{
    /// <summary>
    /// Start a scanned session: connect via <paramref name="connectAsync"/>, arm and scan
    /// <paramref name="drives"/> with <paramref name="profile"/>, and return the session
    /// parked on the scan result - the same behaviour as the shipping
    /// <c>JournalBrokerScanSession.StartAsync</c> with a fake client in place of the
    /// elevated broker.
    /// </summary>
    /// <param name="connectAsync">
    /// Factory yielding a fresh, connected <see cref="JournalBrokerClient"/>. The session
    /// takes exclusive ownership of the returned client and disposes it; return a new
    /// client per call, never a shared one.
    /// </param>
    /// <param name="drives">Drives to arm and scan.</param>
    /// <param name="profile">Cold-scan record profile.</param>
    /// <param name="keepFileNames">
    /// Non-directory file names to keep alongside every directory record; consulted only
    /// under <see cref="BrokerScanProfile.DirectoryIndex"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the connect and scan.</param>
    public static Task<JournalBrokerScanSession> StartScannedAsync(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default) =>
        JournalBrokerScanSession.StartAsync(connectAsync, drives, profile, keepFileNames, cancellationToken);

    /// <summary>
    /// Start a warm session from persisted <paramref name="cursorsByDrive"/>: connect via
    /// <paramref name="connectAsync"/> and park directly on the supplied cursors with no
    /// scan - the same behaviour as the shipping
    /// <c>JournalBrokerScanSession.StartFromCursorsAsync</c> with a fake client in place of
    /// the elevated broker. <see cref="JournalBrokerScanSession.LatestScan"/> stays null
    /// until the first rescan; <paramref name="profile"/> and
    /// <paramref name="keepFileNames"/> apply to a later rescan.
    /// </summary>
    /// <param name="connectAsync">
    /// Factory yielding a fresh, connected <see cref="JournalBrokerClient"/>. The session
    /// takes exclusive ownership of the returned client and disposes it; return a new
    /// client per call, never a shared one.
    /// </param>
    /// <param name="cursorsByDrive">Per-drive resume cursors to park on.</param>
    /// <param name="profile">Cold-scan record profile a later rescan uses.</param>
    /// <param name="keepFileNames">Keep-file names a later rescan uses.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public static Task<JournalBrokerScanSession> StartFromCursorsAsync(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default) =>
        JournalBrokerScanSession.StartFromCursorsAsync(connectAsync, cursorsByDrive, profile, keepFileNames, cancellationToken);
}
