using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// The scan-drive mode: one drive scanned into a block through BrokerProcess.ScanDriveAsync, with
// no FileIndex. This process stays unelevated; the broker it launches asks for elevation.
partial class DriveScanner
{
    internal Func<CancellationToken, Task<BrokerProcess>> _launchBroker = LaunchBrokerNative;
    internal Func<string, uint> _readVolumeSerial = ReadVolumeSerialNative;

    internal Func<char, string> _createBlockPath = letter =>
        Path.Combine(Path.GetTempPath(), $"mftlib-testprogram-{letter}-{Guid.NewGuid():N}.mlix");

    static Task<BrokerProcess> LaunchBrokerNative(CancellationToken cancellationToken)
    {
        return OperatingSystem.IsWindows()
            ? BrokerProcess.LaunchAsync(BrokerLauncher.Launch, cancellationToken)
            : throw new PlatformNotSupportedException("The broker is Windows only.");
    }

    static uint ReadVolumeSerialNative(string letter)
    {
        return OperatingSystem.IsWindows()
            ? IndexedDrive.FromWindowsVolume(letter).VolumeSerial
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
        foreach (var drive in drives)
        {
            await ScanDriveThroughBrokerAsync(broker, drive, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task ScanDriveThroughBrokerAsync(BrokerProcess broker, string drive, CancellationToken cancellationToken)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            var target = new BlockScanTarget(_createBlockPath(char.ToUpperInvariant(letter[0])),
                _readVolumeSerial(letter), DeleteOnClose: true);
            var options = new BrokerScanOptions { Progress = new PhaseReporter(_writeLine) };

            var result = await broker.ScanDriveAsync(letter[0], target, options, cancellationToken)
                .ConfigureAwait(false);
            using var block = result.Block.Block;

            _writeLine($"Block holds {block.Header.RowCount} rows; {result.Block.SkippedRecordCount} records skipped");
            _writeLine($"Armed cursor: journal {result.ArmedCursor.JournalId} at USN {result.ArmedCursor.NextUsn}");
            _writeLine(result.AdvancedCursor is { } advanced
                ? $"Catch-up held; advanced cursor at USN {advanced.NextUsn}"
                : $"Catch-up lost: {result.CatchUpLoss?.Cause}");
            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }

    // Reports each scan phase once, on the reporting thread, so the output keeps its order.
    sealed class PhaseReporter(Action<string> writeLine) : IProgress<BrokerScanProgress>
    {
        BrokerScanPhase? _lastPhase;

        public void Report(BrokerScanProgress value)
        {
            if (_lastPhase == value.Phase)
            {
                return;
            }

            _lastPhase = value.Phase;
            writeLine($"  {value.Phase}: {value.RecordsProcessed} records");
        }
    }
}
