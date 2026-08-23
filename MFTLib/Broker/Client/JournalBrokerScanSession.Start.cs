using System.Runtime.Versioning;

namespace MFTLib;

public sealed partial class JournalBrokerScanSession
{
    /// <summary>
    ///     Spawn one elevated broker (single UAC prompt via <paramref name="launchBroker" />),
    ///     arm and scan <paramref name="drives" /> with <see cref="BrokerScanProfile.Full" />,
    ///     and return a session parked on the result. Throws
    ///     <see cref="InvalidOperationException" /> if the broker declines to launch or
    ///     dies before the scan completes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(launchBroker, drives, BrokerScanProfile.Full, static (_, _) => ValueTask.CompletedTask, null,
            cancellationToken);
    }

    /// <summary>
    ///     Spawn one elevated broker (single UAC prompt via <paramref name="launchBroker" />),
    ///     arm and scan <paramref name="drives" /> with <see cref="BrokerScanProfile.Full" />,
    ///     streaming records to <paramref name="consumeRecords" />,
    ///     and return a session parked on the result. Throws
    ///     <see cref="InvalidOperationException" /> if the broker declines to launch or
    ///     dies before the scan completes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        ScanRecordBatchConsumer consumeRecords,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(launchBroker, drives, BrokerScanProfile.Full, consumeRecords, null, cancellationToken);
    }

    /// <summary>
    ///     As <see cref="StartAsync(Func{string,bool},IReadOnlyList{string},CancellationToken)" />
    ///     but with an explicit <paramref name="profile" /> and, under
    ///     <see cref="BrokerScanProfile.DirectoryIndex" />, an optional set of non-directory
    ///     <paramref name="keepFileNames" /> to keep alongside every directory record.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(
            cancellation => JournalBrokerClient.SpawnAndConnectAsync(launchBroker, cancellation),
            drives, profile, static (_, _) => ValueTask.CompletedTask, keepFileNames, cancellationToken);
    }

    /// <summary>
    ///     As <see cref="StartAsync(Func{string,bool},IReadOnlyList{string},ScanRecordBatchConsumer,CancellationToken)" />
    ///     but with an explicit <paramref name="profile" /> and, under
    ///     <see cref="BrokerScanProfile.DirectoryIndex" />, an optional set of non-directory
    ///     <paramref name="keepFileNames" /> to keep alongside every directory record.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        ScanRecordBatchConsumer consumeRecords,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(
            cancellation => JournalBrokerClient.SpawnAndConnectAsync(launchBroker, cancellation),
            drives, profile, consumeRecords, keepFileNames, cancellationToken);
    }

    /// <summary>
    ///     Spawn one elevated broker (single UAC prompt via <paramref name="launchBroker" />),
    ///     arm and scan <paramref name="drives" /> with caller-specified <paramref name="options" />
    ///     (profile, consumer, keepFileNames, progress), and return a session parked on the result.
    ///     Throws <see cref="InvalidOperationException" /> if the broker declines to launch or
    ///     dies before the scan completes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        BrokerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(
            cancellation => JournalBrokerClient.SpawnAndConnectAsync(launchBroker, cancellation),
            drives, options, cancellationToken);
    }

    // The public overloads above delegate here with connectAsync set to
    // JournalBrokerClient.SpawnAndConnectAsync. Tests inject a fake client built
    // over an in-memory duplex stream.
    internal static Task<JournalBrokerScanSession> StartAsync(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(connectAsync, drives, new BrokerScanOptions
        {
            Profile = profile,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    internal static Task<JournalBrokerScanSession> StartAsync(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        ScanRecordBatchConsumer consumeRecords,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(connectAsync, drives, new BrokerScanOptions
        {
            Profile = profile,
            ConsumeRecords = consumeRecords,
            KeepFileNames = keepFileNames
        }, cancellationToken);
    }

    internal static async Task<JournalBrokerScanSession> StartAsync(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        IReadOnlyList<string> drives,
        BrokerScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = await connectAsync(cancellationToken).ConfigureAwait(false);
        var profile = options?.Profile ?? BrokerScanProfile.Full;
        var keepFileNames = options?.KeepFileNames;
        var session = new JournalBrokerScanSession(client, drives, profile, keepFileNames, EmptyCursors);
        try
        {
            var result = await client.ArmScanAndCatchUpAsync(
                drives,
                options,
                cancellationToken).ConfigureAwait(false);
            lock (session._stateLock)
            {
                if (!session._isFaulted)
                {
                    session._latestScan = result;
                    session._watchCursors = result.AdvancedCursors;
                    session._state = JournalBrokerSessionState.Parked;
                }
            }
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (session.TryGetFaultReason(out var reason))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(reason);
        }

        return session;
    }

    /// <summary>
    ///     Spawn one elevated broker (single UAC prompt via <paramref name="launchBroker" />)
    ///     and return a session parked on <paramref name="cursorsByDrive" /> without scanning -
    ///     a warm start for a consumer that already holds a cached inventory and only needs to
    ///     resume watching. <see cref="StartWatchAsync" /> watches from these cursors; a cursor
    ///     with <c>JournalId</c> 0 means "watch from the drive's current position". No scan runs
    ///     until the first <see cref="RescanAsync(CancellationToken)" />, so <see cref="LatestScan" /> is null until
    ///     then. <see cref="BrokerScanProfile.Full" /> and no keep-file names apply to a later
    ///     <see cref="RescanAsync(CancellationToken)" />; use the overload below to set them.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartFromCursorsAsync(
        Func<string, bool> launchBroker,
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        CancellationToken cancellationToken = default)
    {
        return StartFromCursorsAsync(launchBroker, cursorsByDrive, BrokerScanProfile.Full,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     As
    ///     <see cref="StartFromCursorsAsync(Func{string,bool},IReadOnlyDictionary{string,UsnJournalCursor},CancellationToken)" />
    ///     but with the explicit <paramref name="profile" /> and optional
    ///     <paramref name="keepFileNames" /> a later <see cref="RescanAsync(CancellationToken)" /> uses.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartFromCursorsAsync(
        Func<string, bool> launchBroker,
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        return StartFromCursorsAsync(
            cancellation => JournalBrokerClient.SpawnAndConnectAsync(launchBroker, cancellation),
            cursorsByDrive, profile, keepFileNames, cancellationToken);
    }

    // Warm-start seam mirroring the internal StartAsync seam: connect the same way, but park
    // directly on the caller's cursors with no arm-and-scan. Tests inject a fake client built
    // over an in-memory duplex stream.
    internal static async Task<JournalBrokerScanSession> StartFromCursorsAsync(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        var watchCursors = NormalizeCursors(cursorsByDrive);
        var client = await connectAsync(cancellationToken).ConfigureAwait(false);
        // Drives default for a later no-argument RescanAsync: the warm-start volumes.
        return new JournalBrokerScanSession(
            client, watchCursors.Keys.ToArray(), profile, keepFileNames, watchCursors);
    }

    // Re-key the caller's cursors by bare drive letter (case-insensitive) so the watch spec
    // and the per-drive WatchDriveAsync lookup agree with the scan path's keying.
    static Dictionary<string, UsnJournalCursor> NormalizeCursors(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive)
    {
        var normalized = new Dictionary<string, UsnJournalCursor>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in cursorsByDrive)
        {
            normalized[JournalBrokerClient.NormalizeDriveLetter(pair.Key)] = pair.Value;
        }

        return normalized;
    }

    bool TryGetFaultReason(out string? reason)
    {
        lock (_stateLock)
        {
            reason = _faultReason;
            return _isFaulted;
        }
    }
}
