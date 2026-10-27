using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Creates the control pipe, launches the elevated broker against it, and waits for it to connect.</summary>
sealed partial class BrokerProcess
{
    // Internal launch seams let tests drive the connection deadline with fake time.
    internal static TimeSpan _connectTimeout = BrokerLiveness.DefaultConnectTimeout;
    internal static TimeProvider _connectTimeProvider = TimeProvider.System;

    internal static void ResetToDefaults()
    {
        _connectTimeout = BrokerLiveness.DefaultConnectTimeout;
        _connectTimeProvider = TimeProvider.System;
    }

    /// <summary>
    ///     Creates the control pipe, launches the elevated broker against it, and waits up to
    ///     <paramref name="connectTimeout" /> for the broker to connect. Scans write into the sections
    ///     <paramref name="createBlockSection" /> creates; production passes
    ///     <see cref="CreateRealDriveBlockSection" />, whose named sections only Windows supports.
    /// </summary>
    /// <param name="launchBroker">
    ///     Receives the broker command line (<c>--broker --pipe NAME</c>, plus the diagnostics flags
    ///     when diagnostics are on) and returns whether the launch started: false when the UAC
    ///     prompt was declined. Production passes <see cref="BrokerLauncher.Launch" />.
    /// </param>
    /// <param name="connectTimeout">
    ///     How long to wait for the broker to connect; non-negative or infinite. Null waits the default 30 seconds.
    /// </param>
    /// <param name="createBlockSection">Creates the block section each scan writes into.</param>
    /// <param name="cancellationToken">Stops waiting for the connection.</param>
    /// <exception cref="InvalidOperationException">The launch did not start.</exception>
    /// <exception cref="TimeoutException">The broker launched but never connected.</exception>
    internal static async Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, TimeSpan? connectTimeout,
        BrokerBlockSectionFactory createBlockSection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchBroker);
        var timeout = connectTimeout ?? _connectTimeout;
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), timeout,
                "Connect timeout must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        var pipeName = BrokerFrame.PipeNamePrefix + Guid.NewGuid().ToString("N");
        NamedPipeServerStream? server = BrokerPipeAccess.CreateServer(pipeName);
        try
        {
            if (!launchBroker(FormattableString.Invariant($"--broker --pipe {pipeName}{DiagnosticsArguments()}")))
            {
                throw new InvalidOperationException(
                    "Failed to launch the elevated broker (the UAC prompt was declined?)");
            }

            await WaitForBrokerAsync(server, pipeName, timeout, _connectTimeProvider, cancellationToken)
                .ConfigureAwait(false);
            var process = new BrokerProcess(server, new NamedPipeBrokerPipeFactory(), createBlockSection,
                TimeProvider.System);
            server = null;
            return process;
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // A runas launch does not reliably inherit MFTLIB_BROKER_DIAG, so the diagnostics flag is
    // forwarded explicitly. This process's log path travels alongside as --diag-log (quoted: temp
    // paths can contain spaces) so the broker uses the same directory and filters both logs' journal entries, and
    // MFTLIB_BROKER_DIAG_INCLUDE_SELF crosses as --diag-include-self for the same reason.
    static string DiagnosticsArguments()
    {
        if (!BrokerDiagnostics.Enabled)
        {
            return string.Empty;
        }

        var arguments = FormattableString.Invariant($" --diag --diag-log \"{BrokerDiagnostics.LogPath}\"");
        return BrokerDiagnostics.IncludeSelfEntries ? arguments + " --diag-include-self" : arguments;
    }

    static async Task WaitForBrokerAsync(NamedPipeServerStream server, string pipeName, TimeSpan connectTimeout,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(connectTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            await server.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            var duration = connectTimeout.TotalSeconds >= 1
                ? $"{BrokerLiveness.FormatSeconds(connectTimeout)}s"
                : FormattableString.Invariant($"{connectTimeout.TotalMilliseconds:G}ms");
            throw new TimeoutException(FormattableString.Invariant(
                $"Timed out waiting {duration} for the elevated broker to connect to pipe '{pipeName}'. The broker process was launched, but never connected (headless session, unserviced UAC prompt, or broker crash before connect)."));
        }
    }

    // The returned Lifetime is the MemoryMappedFile the Block already holds (see
    // NamedBlockSection.Create), so disposing it once the section is written only unpublishes the
    // name; the Block's own view keeps the mapping alive.
    [SupportedOSPlatform("windows")]
    internal static (string SectionName, BlockFile Block, IDisposable Lifetime) CreateRealDriveBlockSection(
        VolumeKey drive, BlockFileCreateOptions options)
    {
        var sectionName = NamedBlockSection.BuildSectionName(drive);
        var (block, lifetime) = NamedBlockSection.Create(options, sectionName);
        return (sectionName, block, lifetime);
    }
}
