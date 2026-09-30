using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Creates the control pipe, launches the elevated broker against it, and waits for it to connect.</summary>
public sealed partial class BrokerProcess
{
    /// <summary>How long <see cref="LaunchAsync(Func{string, bool}, CancellationToken)" /> waits for the broker to connect.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    // Internal launch seams let tests drive the connection deadline with fake time.
    internal static TimeSpan _connectTimeout = DefaultConnectTimeout;
    internal static TimeProvider _connectTimeProvider = TimeProvider.System;

    internal static void ResetToDefaults()
    {
        _connectTimeout = DefaultConnectTimeout;
        _connectTimeProvider = TimeProvider.System;
    }

    /// <summary>
    ///     Launches the elevated broker and waits up to <see cref="DefaultConnectTimeout" /> for it
    ///     to connect to the control pipe.
    /// </summary>
    /// <inheritdoc cref="LaunchAsync(Func{string, bool}, TimeSpan, CancellationToken)" />
    [SupportedOSPlatform("windows")]
    public static Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, CancellationToken cancellationToken)
    {
        return LaunchAsync(launchBroker, _connectTimeout, cancellationToken);
    }

    /// <summary>
    ///     Creates the control pipe, launches the elevated broker against it, and waits up to
    ///     <paramref name="connectTimeout" /> for the broker to connect. Scans write into real named
    ///     block sections.
    /// </summary>
    /// <param name="launchBroker">
    ///     Receives the broker command line (<c>--broker --pipe NAME</c>, plus the diagnostics flags
    ///     when diagnostics are on) and returns whether the launch started: false when the UAC
    ///     prompt was declined. Production passes <see cref="BrokerLauncher.Launch" />.
    /// </param>
    /// <param name="connectTimeout">How long to wait for the broker to connect; non-negative or infinite.</param>
    /// <param name="cancellationToken">Stops waiting for the connection.</param>
    /// <exception cref="InvalidOperationException">The launch did not start.</exception>
    /// <exception cref="TimeoutException">The broker launched but never connected.</exception>
    [SupportedOSPlatform("windows")]
    public static async Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchBroker);
        if (connectTimeout < TimeSpan.Zero && connectTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), connectTimeout,
                "Connect timeout must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        var pipeName = "mftlib-broker-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream? server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        try
        {
            if (!launchBroker(FormattableString.Invariant($"--broker --pipe {pipeName}{DiagnosticsArguments()}")))
            {
                throw new InvalidOperationException(
                    "Failed to launch the elevated broker (the UAC prompt was declined?)");
            }

            await WaitForBrokerAsync(server, pipeName, connectTimeout, _connectTimeProvider, cancellationToken)
                .ConfigureAwait(false);
            var process = new BrokerProcess(server, new NamedPipeBrokerPipeFactory(), CreateRealDriveBlockSection,
                TimeProvider.System, pipeName);
            server = null;
            return process;
        }
        finally
        {
            server?.Dispose();
        }
    }

    // A runas launch does not reliably inherit MFTLIB_BROKER_DIAG, so the diagnostics flag is
    // forwarded explicitly. This process's log path travels alongside as --diag-log (quoted: temp
    // paths can contain spaces) so the broker can filter both logs' journal entries, and
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
                ? FormattableString.Invariant($"{connectTimeout.TotalSeconds:G}s")
                : FormattableString.Invariant($"{connectTimeout.TotalMilliseconds:G}ms");
            throw new TimeoutException(FormattableString.Invariant(
                $"Timed out waiting {duration} for the elevated broker to connect to pipe '{pipeName}'. The broker process was launched, but never connected (headless session, unserviced UAC prompt, or broker crash before connect)."));
        }
    }

    // The returned Lifetime is the MemoryMappedFile the Block already holds (see
    // NamedBlockSection.Create), so disposing it once the section is written only unpublishes the
    // name; the Block's own view keeps the mapping alive.
    [SupportedOSPlatform("windows")]
    static (string SectionName, BlockFile Block, IDisposable Lifetime) CreateRealDriveBlockSection(
        char driveLetter, BlockFileCreateOptions options)
    {
        var sectionName = NamedBlockSection.BuildSectionName(driveLetter);
        var (block, lifetime) = NamedBlockSection.Create(options, sectionName);
        return (sectionName, block, lifetime);
    }
}
