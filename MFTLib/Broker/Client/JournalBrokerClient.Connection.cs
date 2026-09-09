using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    /// <summary>
    ///     Default timeout for waiting for the elevated broker child process to connect
    ///     to the named pipe after launching (30 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    // How long SpawnAndConnectAsync waits for the elevated child process to connect
    // to the named pipe before throwing TimeoutException. Internal and mutable so tests
    // can shrink the window instead of waiting for the real production timeout.
    internal static TimeSpan _connectTimeout = DefaultConnectTimeout;

    /// <summary>
    ///     Reset internal timeout configuration and seams to their default values.
    /// </summary>
    internal static void ResetToDefaults()
    {
        _connectTimeout = DefaultConnectTimeout;
        _endWatchAckTimeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    ///     Build the named pipe, launch the elevated broker against it, wait for the broker
    ///     to connect within <see cref="DefaultConnectTimeout" /> (or <c>_connectTimeout</c>), and
    ///     return a ready client that creates named block sections for each drive.
    ///     <paramref name="launchBroker" /> receives the broker command line (e.g. "--broker --pipe NAME")
    ///     and returns whether the launch started (false if the user declined the UAC prompt).
    ///     Throws <see cref="TimeoutException" /> if the broker is launched but does not connect within the timeout.
    ///     Production passes <see cref="BrokerLauncher.Launch" />; tests pass a fake.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerClient> SpawnAndConnectAsync(
        Func<string, bool> launchBroker, CancellationToken cancellationToken = default)
    {
        return SpawnAndConnectAsync(launchBroker, _connectTimeout, cancellationToken);
    }

    /// <summary>
    ///     Build the named pipe, launch the elevated broker against it, wait up to
    ///     <paramref name="connectTimeout" /> for the broker to connect, and return a ready client wired
    ///     to the real named block section factory.
    ///     <paramref name="launchBroker" /> receives the broker command line (e.g. "--broker --pipe NAME")
    ///     and returns whether the launch started (false if the user declined the UAC prompt).
    ///     Throws <see cref="TimeoutException" /> if the broker is launched but does not connect within the timeout.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<JournalBrokerClient> SpawnAndConnectAsync(
        Func<string, bool> launchBroker, TimeSpan connectTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchBroker);
        if (connectTimeout < TimeSpan.Zero && connectTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeout),
                connectTimeout,
                "Connect timeout must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        var pipeName = "mftlib-broker-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream? server = new(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            // Propagate the diagnostics flag to the elevated child explicitly: a runas
            // launch does not reliably inherit the MFTLIB_BROKER_DIAG env var.
            var diagFlag = Environment.GetEnvironmentVariable("MFTLIB_BROKER_DIAG") == "1"
                ? " --diag"
                : string.Empty;
            if (!launchBroker(FormattableString.Invariant($"--broker --pipe {pipeName}{diagFlag}")))
            {
                throw new InvalidOperationException(
                    "Failed to launch the elevated broker (the UAC prompt was declined?)");
            }

            using var timeoutCts = new CancellationTokenSource(connectTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                await server.WaitForConnectionAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var durationStr = connectTimeout.TotalSeconds >= 1
                    ? FormattableString.Invariant($"{connectTimeout.TotalSeconds:G}s")
                    : FormattableString.Invariant($"{connectTimeout.TotalMilliseconds:G}ms");
                throw new TimeoutException(FormattableString.Invariant(
                    $"Timed out waiting {durationStr} for the elevated broker to connect to pipe '{pipeName}'. The broker process was launched, but never connected (headless session, unserviced UAC prompt, or broker crash before connect)."));
            }

            var client = new JournalBrokerClient(server, CreateRealDriveBlockSection);
            server = null;
            return client;
        }
        finally
        {
            server?.Dispose();
        }
    }

    // The returned Lifetime is the same MemoryMappedFile instance already held by the
    // returned Block (see NamedBlockSection.Create), not an independent resource. Disposing
    // it early, as the block scan arm does on ScanReady, only unpublishes the named section
    // so no other process can open it by name; it does not tear down the mapping, because the
    // Block's own view accessor holds its own reference and Windows keeps a memory-mapped
    // section alive while any view of it remains mapped. The Block disposes the same
    // MemoryMappedFile again in its own Dispose, which is safe because a second dispose of an
    // already-disposed safe handle is a no-op. A future implementation of this seam must
    // preserve that property: it must never return a lifetime whose disposal also invalidates
    // the block's view, since callers are entitled to dispose the lifetime while still using
    // the block.
    [SupportedOSPlatform("windows")]
    static (string SectionName, BlockFile Block, IDisposable Lifetime) CreateRealDriveBlockSection(
        string driveLetter, BlockFileCreateOptions options)
    {
        var sectionName = NamedBlockSection.BuildSectionName(driveLetter[0]);
        var (block, lifetime) = NamedBlockSection.Create(options, sectionName);
        return (sectionName, block, lifetime);
    }
}
