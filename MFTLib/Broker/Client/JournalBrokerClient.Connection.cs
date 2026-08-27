using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.Versioning;

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
    ///     return a ready client wired to the real MMF reader and a page-file-backed per-drive MMF creator.
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
    ///     to the real MMF reader and a page-file-backed per-drive MMF creator.
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
        var server = new NamedPipeServerStream(
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

            return new JournalBrokerClient(server, new RealMmfReader(), CreateRealDriveMmf);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Production createDriveMmf: a uniquely named, page-file-backed map the elevated
    // broker opens by name and writes the cold scan into. The MemoryMappedFile handle
    // is the lifetime the client disposes once the scan has been read back.
    //
    // Deliberately NOT MemoryMappedFileOptions.DelayAllocatePages (SEC_RESERVE): a spike
    // (see MFTLib#89) confirmed that pages reserved this way are not committed on first
    // touch the way an ordinary SEC_COMMIT section's pages are. Writing through the
    // stream-based view (CreateViewStream + Stream.Write, which is how RealMmfWriter and
    // ScanPayload.Write operate) crashed the process with an unrecoverable
    // AccessViolationException, even for a 1 KiB write to a mostly-empty map - the OS does
    // not auto-commit SEC_RESERVE pages on write the way it does SEC_COMMIT pages; that
    // requires explicit VirtualAlloc(MEM_COMMIT) calls tracking a write high-water mark,
    // which is a materially bigger, riskier change than a flag swap and is not something
    // the existing Stream-based writer seam supports. Capacity is therefore sized via the
    // caller-controlled "capacity" parameter (see BrokerScanOptions.MmfCapacityBytes)
    // rather than by requesting a huge reservation and hoping it stays cheap.
    [SupportedOSPlatform("windows")]
    static (string Name, IDisposable Lifetime) CreateRealDriveMmf(string driveLetter, long capacity)
    {
        var name = "mftlib-scan-" + driveLetter + "-" + Guid.NewGuid().ToString("N");
        var map = MemoryMappedFile.CreateNew(name, capacity);
        return (name, map);
    }
}
