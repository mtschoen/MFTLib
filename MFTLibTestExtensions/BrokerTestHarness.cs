using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     Runs a <see cref="JournalBrokerHost" /> in process and returns a <see cref="BrokerProcess" />
///     connected to it, so consumer tests drive the real client and the real host over
///     in-memory pipes with no elevation and no child process.
/// </summary>
public static class BrokerTestHarness
{
    /// <summary>
    ///     Starts <paramref name="host" />'s session on a background task over an in-memory
    ///     control pipe; every drive pipe the returned process opens is connected in memory by
    ///     name. Disposing the process ends the host's session and waits for it to return.
    /// </summary>
    /// <param name="host">The host to serve; its sources are the test's fakes.</param>
    /// <param name="blockSectionWriter">Writes each scan into the section the client created.</param>
    /// <param name="createBlockSection">Creates the client's block section for each scan.</param>
    public static BrokerProcess StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection)
    {
        return StartInProcess(host, blockSectionWriter, createBlockSection, new BrokerTestHarnessOptions());
    }

    /// <inheritdoc cref="StartInProcess(JournalBrokerHost, IBlockSectionWriter, BrokerBlockSectionFactory)" />
    /// <param name="host">The host to serve; its sources are the test's fakes.</param>
    /// <param name="blockSectionWriter">Writes each scan into the section the client created.</param>
    /// <param name="createBlockSection">Creates the client's block section for each scan.</param>
    /// <param name="options">The client's clock, per-pipe connection failures and per-pipe held host writes.</param>
    public static BrokerProcess StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection, BrokerTestHarnessOptions options)
    {
        return Start(host, blockSectionWriter, createBlockSection, options, null, null);
    }

    // The same start with two more seams for MFTLib's own tests: a wrapper around each client
    // end, by pipe name, and a wrapper around the host's connector.
    internal static BrokerProcess Start(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection, BrokerTestHarnessOptions options,
        Func<string, Stream, Stream>? wrapClientStream, Func<BrokerChannelConnector, BrokerChannelConnector>? wrapConnector)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(blockSectionWriter);
        ArgumentNullException.ThrowIfNull(createBlockSection);
        ArgumentNullException.ThrowIfNull(options);

        var pipes = new InMemoryBrokerPipes(options, wrapClientStream);
        var (clientControl, hostControl) = pipes.CreatePair(InMemoryBrokerPipes.ControlPipeName);
        BrokerChannelConnector connector = pipes.ConnectAsync;
        connector = wrapConnector?.Invoke(connector) ?? connector;
        var serve = Task.Run(() => host.ServeAsync(hostControl, connector, blockSectionWriter, CancellationToken.None));
        var exited = serve.ContinueWith(ended => ExitHost(ended, pipes), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new BrokerProcess(new HostLifetimeStream(clientControl, exited), pipes, createBlockSection,
            options.TimeProvider);
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
