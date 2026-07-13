using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    /// <summary>
    /// Build the named pipe, launch the elevated broker against it, wait for the broker
    /// to connect, and return a ready client wired to the real MMF reader and a
    /// page-file-backed per-drive MMF creator. <paramref name="launchBroker"/> receives
    /// the broker command line (e.g. "--broker --pipe NAME") and returns whether the
    /// launch started (false if the user declined the UAC prompt). Production passes
    /// <see cref="BrokerLauncher.Launch"/>; tests pass a fake.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<JournalBrokerClient> SpawnAndConnectAsync(
        Func<string, bool> launchBroker, CancellationToken cancellationToken = default)
    {
        var pipeName = "mftlib-broker-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            // Propagate the diagnostics flag to the elevated child explicitly: a runas
            // launch does not reliably inherit the MFTLIB_BROKER_DIAG env var.
            var diagFlag = Environment.GetEnvironmentVariable("MFTLIB_BROKER_DIAG") == "1"
                ? " --diag" : string.Empty;
            if (!launchBroker(FormattableString.Invariant($"--broker --pipe {pipeName}{diagFlag}")))
                throw new InvalidOperationException(
                    "Failed to launch the elevated broker (the UAC prompt was declined?)");

            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
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
    [SupportedOSPlatform("windows")]
    static (string Name, IDisposable Lifetime) CreateRealDriveMmf(string driveLetter, long capacity)
    {
        var name = "mftlib-scan-" + driveLetter + "-" + Guid.NewGuid().ToString("N");
        var map = MemoryMappedFile.CreateNew(name, capacity);
        return (name, map);
    }

}
