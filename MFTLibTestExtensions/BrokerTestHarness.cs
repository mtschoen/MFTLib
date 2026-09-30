using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Runs a <see cref="JournalBrokerHost" /> in process and returns a handle whose
///     <see cref="BrokerProcess" /> is connected to it, so consumer tests drive the real client and the real host over
///     in-memory pipes with no elevation and no child process.
/// </summary>
public static class BrokerTestHarness
{
    /// <summary>
    ///     Starts <paramref name="host" />'s session on a background task over an in-memory
    ///     control pipe; every drive pipe the returned process opens is connected in memory by
    ///     name. Disposing the handle disposes the process, which ends the host's session and
    ///     waits for it to return.
    /// </summary>
    /// <param name="host">The host to serve; its sources are the test's fakes.</param>
    /// <param name="blockSectionWriter">Writes each scan into the section the client created.</param>
    /// <param name="createBlockSection">Creates the client's block section for each scan.</param>
    public static InProcessBrokerHandle StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection)
    {
        return StartInProcess(host, blockSectionWriter, createBlockSection, new BrokerTestHarnessOptions());
    }

    /// <inheritdoc cref="StartInProcess(JournalBrokerHost, IBlockSectionWriter, BrokerBlockSectionFactory)" />
    /// <param name="host">The host to serve; its sources are the test's fakes.</param>
    /// <param name="blockSectionWriter">Writes each scan into the section the client created.</param>
    /// <param name="createBlockSection">Creates the client's block section for each scan.</param>
    /// <param name="options">The client's clock, per-pipe connection failures and per-pipe held host writes.</param>
    public static InProcessBrokerHandle StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection, BrokerTestHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(blockSectionWriter);
        ArgumentNullException.ThrowIfNull(createBlockSection);
        return Start(host, blockSectionWriter, createBlockSection, options, null, null);
    }

    /// <summary>
    ///     Starts <paramref name="host" />'s session with no block-section seams, for tests that
    ///     exercise only control operations. A scan fails clearly: the client has no section
    ///     factory, so <see cref="BrokerProcess.ScanDriveAsync" /> throws
    ///     <see cref="InvalidOperationException" /> before it opens a channel.
    /// </summary>
    /// <param name="host">The host to serve; its sources are the test's fakes.</param>
    public static InProcessBrokerHandle StartInProcess(JournalBrokerHost host)
    {
        return StartInProcess(host, new BrokerTestHarnessOptions());
    }

    /// <inheritdoc cref="StartInProcess(JournalBrokerHost)" />
    /// <param name="host">The host to serve; its sources are the test's fakes.</param>
    /// <param name="options">The client's clock, per-pipe connection failures and per-pipe held host writes.</param>
    public static InProcessBrokerHandle StartInProcess(JournalBrokerHost host, BrokerTestHarnessOptions options)
    {
        return Start(host, null, RefuseBlockSection, options, null, null);
    }

    static (string SectionName, BlockFile Block, IDisposable Lifetime) RefuseBlockSection(char driveLetter,
        BlockFileCreateOptions options)
    {
        throw new InvalidOperationException(
            $"Drive {driveLetter} scan needs a block section, but this in-process broker was started without a block section factory.");
    }

    // The same start with two more seams for MFTLib's own tests: a wrapper around each client
    // end, by pipe name, and a wrapper around the host's connector.
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
