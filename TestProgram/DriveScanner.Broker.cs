using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// The scan-drive mode: one drive at a time opened as a NoCache FileIndex over the broker, the path
// every consumer ships. This process stays unelevated; the broker it launches asks for elevation.
partial class DriveScanner
{
    internal Func<CancellationToken, Task<BrokerProcess>> _launchBroker = LaunchBrokerNative;
    internal Func<string, IndexedDrive> _resolveDrive = ResolveDriveNative;

    // Where the index keeps its cache folder; null selects the library's default location.
    internal string? _cacheDirectory;

    static Task<BrokerProcess> LaunchBrokerNative(CancellationToken cancellationToken)
    {
        return OperatingSystem.IsWindows()
            ? BrokerProcess.LaunchAsync(BrokerLauncher.Launch, cancellationToken)
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
        BrokerProcess broker;
        try
        {
            broker = await _launchBroker(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _writeLine($"Error launching the broker: {exception.Message}");
            return;
        }

        await using var ownedBroker = broker.ConfigureAwait(false);
        var source = new BrokerMftBlockProducer(_ => Task.FromResult(broker)).CreateIndexSource();
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

    void WriteStatus(DriveStatus status)
    {
        if (status.State == DriveState.Failed)
        {
            throw new InvalidOperationException(status.MftProducerFailureMessage ?? $"The drive failed: {status.FailureKind}");
        }

        // An offline drive settles without a scan, so there is no catch-up to report.
        if (status.State == DriveState.Offline)
        {
            throw new InvalidOperationException("The drive is offline; nothing was scanned.");
        }

        _writeLine($"Index holds {status.LiveRowCount} rows; {status.SkippedRecordCount} records skipped");
        _writeLine(status.CheckpointLoss is { } loss
            ? $"Catch-up lost: {loss.Cause}"
            : $"Catch-up held; watch supported: {status.WatchSupported}");
    }

    // Reports each scan phase once, on the reporting thread, so the output keeps its order.
    sealed class PhaseReporter(Action<string> writeLine) : IProgress<IndexScanProgress>
    {
        IndexScanPhase? _lastPhase;

        public void Report(IndexScanProgress value)
        {
            if (_lastPhase == value.Phase)
            {
                return;
            }

            _lastPhase = value.Phase;
            writeLine($"  {value.Phase}: {value.RowsWritten} rows");
        }
    }
}
