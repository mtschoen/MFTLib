using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

// The scan-drive mode: one drive at a time opened as a NoCache FileIndex over the broker, the path
// every consumer ships. This process stays unelevated; the broker it launches asks for elevation.
partial class SampleHost
{
    internal Func<BrokerSession> _createBrokerSession = CreateBrokerSessionNative;
    internal Func<string, IndexedDrive> _resolveDrive = ResolveDriveNative;

    // Where the index keeps its cache folder; null selects the library's default location.
    internal string? _cacheDirectory;

    static BrokerSession CreateBrokerSessionNative()
    {
        return OperatingSystem.IsWindows()
            ? new BrokerSession()
            : throw new PlatformNotSupportedException("The broker is Windows only.");
    }

    static IndexedDrive ResolveDriveNative(string letter)
    {
        return OperatingSystem.IsWindows()
            ? IndexedDrive.FromWindowsVolume(letter)
            : throw new PlatformNotSupportedException("Volume serials are read on Windows only.");
    }

    // One broker, so one elevation prompt, serves every drive of the run.
    internal async Task ScanDrivesThroughBrokerAsync(IReadOnlyList<string> drives, CancellationToken cancellationToken)
    {
        BrokerSession session;
        try
        {
            session = _createBrokerSession();
        }
        catch (Exception exception)
        {
            _writeLine($"Error creating broker session: {exception.Message}");
            return;
        }

        await using var ownedSession = session.ConfigureAwait(false);
        var source = session.CreateIndexSource();
        foreach (var drive in drives)
        {
            await ScanDriveThroughIndexAsync(source, drive, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task ScanDriveThroughIndexAsync(MftIndexSource source, string drive, CancellationToken cancellationToken)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            var options = new FileIndexOptions
            {
                Drives = [_resolveDrive(letter)],
                NoCache = true,
                CacheDirectory = _cacheDirectory,
                MftSource = source,
                Progress = new PhaseReporter(_writeLine)
            };

            await using var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
            WriteStatus(index.Drives.Single());
            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }
}
