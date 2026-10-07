using System.Runtime.CompilerServices;
using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     Runs the production broker host in process and returns a handle whose client is connected to it, so
///     consumer tests drive the real client and the real host over in-memory pipes with no elevation and no
///     child process.
/// </summary>
public static class BrokerTestHarness
{
    /// <summary>
    ///     Starts an in-process broker that serves <paramref name="volumes" />, over an in-memory control
    ///     pipe; every drive pipe the returned process opens is connected in memory by name. The
    ///     host is the real broker host, the client creates real block sections, and each
    ///     scan's records are written through the production row writer and filter. Disposing the handle
    ///     disposes the process, which ends the host's session and waits for it to return, then releases the
    ///     sections.
    /// </summary>
    /// <param name="volumes">The fake volumes the host serves.</param>
    /// <returns>The handle a test passes to <see cref="CreateSession" /> to drive the in-process broker.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="volumes" /> is null.</exception>
    public static InProcessBrokerHandle StartInProcess(ScriptedBrokerVolumes volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        var host = CreateHost(volumes);
        // aislop-ignore-next-line IDISP001 -- ownership moves to the returned handle, which disposes it
        var resources = new ScriptedBrokerResources();
        host.ScanStartingForTest = resources.RecordScan;
        return Start(host, resources.Writer, resources.Sections.Create, new BrokerTestHarnessOptions(),
            null, null).Own(resources);
    }

    /// <summary>
    ///     A <see cref="BrokerSession" /> whose launch is <paramref name="launchAsync" />, so a test hands it a
    ///     started in-process broker (or a launch it holds open, fails or delays) instead of
    ///     launching an elevated one. Everything else is the production session: lazy shared launch, sticky end,
    ///     disposal.
    /// </summary>
    /// <param name="launchAsync">
    ///     Called at most once per launch attempt, with a token that only disposal cancels, and returns the started
    ///     in-process broker whose client the session then owns.
    /// </param>
    /// <returns>The session; the test disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launchAsync" /> is null.</exception>
    public static BrokerSession CreateSession(Func<CancellationToken, Task<InProcessBrokerHandle>> launchAsync)
    {
        ArgumentNullException.ThrowIfNull(launchAsync);
        return new BrokerSession(async cancellationToken =>
            (await launchAsync(cancellationToken).ConfigureAwait(false)).Process);
    }

    static JournalBrokerHost CreateHost(ScriptedBrokerVolumes volumes)
    {
        var queryCursor = volumes.QueryJournalCursor;
        var scan = volumes.ScanDrive;
        var readJournal = volumes.ReadJournal ?? NothingNew;
        var watch = volumes.WatchDrive;
        var queryVolume = volumes.QueryVolume ?? SmallVolume;
        var grow = volumes.GrowUsnJournal;
        return new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(
                driveLetter => queryCursor(driveLetter).ToProduction(),
                scan is null
                    ? null
                    : (driveLetter, parseThreads, operation, progress, _, cancellationToken) =>
                        ConvertScan(scan(new ScriptedScan(driveLetter, parseThreads, operation, progress,
                            cancellationToken))),
                scan is null ? null : (driveLetter, since, maximumBufferReads) =>
                    ConvertJournal(readJournal(driveLetter, since.ToSynthetic(), maximumBufferReads)),
                watch is null
                    ? null
                    : (driveLetter, since, _, cancellationToken) =>
                        ConvertWatch(watch(driveLetter, since.ToSynthetic(), cancellationToken), cancellationToken),
                driveLetter => queryVolume(driveLetter),
                grow is null ? null : (driveLetter, maximumSize, allocationDelta) => grow(driveLetter, maximumSize, allocationDelta)));
    }

    static (SyntheticJournalRecord[] Entries, SyntheticJournalCursor Updated) NothingNew(string driveLetter,
        SyntheticJournalCursor since, int maximumBufferReads) => ([], since);

    static IEnumerable<IReadOnlyList<MftRecord>> ConvertScan(
        IEnumerable<IReadOnlyList<SyntheticScanRecord>> batches)
    {
        foreach (var batch in batches)
        {
            yield return [.. batch.Select(record => record.ToProduction())];
        }
    }

    static (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ConvertJournal(
        (SyntheticJournalRecord[] Entries, SyntheticJournalCursor Updated) result) =>
        (result.Entries.ToProduction(), result.Updated.ToProduction());

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> ConvertWatch(
        IAsyncEnumerable<(SyntheticJournalRecord[] Entries, SyntheticJournalCursor Cursor)> batches,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var (entries, cursor) in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return (entries.ToProduction(), cursor.ToProduction());
        }
    }

    static NtfsVolumeInformation SmallVolume(string driveLetter) => new(256 * 1024, 1024);

    // The start MFTLib's own tests use: any host, section writer and client section factory, plus the
    // client's clock, per-pipe connection failures and held host writes, a wrapper around each client end
    // by pipe name, and a wrapper around the host's connector.
    internal static InProcessBrokerHandle Start(JournalBrokerHost host, IBlockSectionWriter? blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection, BrokerTestHarnessOptions options,
        Func<string, Stream, Stream>? wrapClientStream, Func<BrokerChannelConnector, BrokerChannelConnector>? wrapConnector)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        var pipes = new InMemoryBrokerPipes(options, wrapClientStream);
        var (clientControl, hostControl) = pipes.CreatePair(InMemoryBrokerPipes.ControlPipeName);
        BrokerChannelConnector connector = pipes.ConnectAsync;
        connector = wrapConnector?.Invoke(connector) ?? connector;
        var serve = Task.Run(() => host.ServeAsync(hostControl, connector, blockSectionWriter, CancellationToken.None));
        var exited = serve.ContinueWith(ended => ExitHost(ended, pipes), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var process = new BrokerProcess(new HostLifetimeStream(clientControl, exited), pipes, createBlockSection,
            options.TimeProvider);
        return new InProcessBrokerHandle(process, pipes.CloseHostEnds);
    }

    // What an elevated broker process does when its session ends, for any reason: a failed session
    // is recorded in the diagnostics log, which observes the faulted task, and the process exit
    // closes every pipe end it held. The client learns of it only through those pipes, as it does
    // in production.
    static void ExitHost(Task session, InMemoryBrokerPipes pipes)
    {
        if (session.Exception is { } failure)
        {
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel,
                $"Broker session failed: {failure.GetBaseException()}");
        }
        else if (session.IsCanceled)
        {
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "Broker session was cancelled.");
        }

        pipes.CloseHostEnds();
    }
}
