using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

// The index modes: each drive opened as a FileIndex over the broker, the path every consumer ships. scan-drive opens
// NoCache and only reports; the others open the cached index and act on it. This process stays unelevated; the
// broker it launches asks for elevation.
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

    internal Task ScanDrivesThroughBrokerAsync(IReadOnlyList<string> drives, CancellationToken cancellationToken)
    {
        return RunThroughBrokerAsync(new WatchArguments(ProgramMode.ScanDrive, drives), cancellationToken);
    }

    // One broker, so one elevation prompt, serves every drive of the run.
    internal async Task RunThroughBrokerAsync(WatchArguments parsed, CancellationToken cancellationToken)
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
        if (parsed.Mode is not ProgramMode.ScanDrive)
        {
            session.Connecting += () => _writeLine("Broker: connecting; a UAC prompt may follow.");
            session.Connected += () => _writeLine("Broker: connected.");
        }

        var source = session.CreateIndexSource(parsed.ScanOptions);
        foreach (var drive in parsed.Drives)
        {
            await RunOnDriveAsync(session, source, parsed, drive, cancellationToken).ConfigureAwait(false);
        }

        if (session.HasEnded)
        {
            _writeLine($"Broker ended: {await session.Ended.ConfigureAwait(false)}");
        }
    }

    async Task RunOnDriveAsync(BrokerSession session, MftIndexSource source, WatchArguments parsed, string drive, CancellationToken cancellationToken)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            var cached = parsed.Mode is not ProgramMode.ScanDrive;
            var options = new FileIndexOptions
            {
                Drives = [_resolveDrive(letter)],
                NoCache = !cached,
                CacheDirectory = parsed.CacheDirectory ?? _cacheDirectory,
                CacheTag = cached ? parsed.CacheTag : default,
                MftSource = source,
                Progress = new PhaseReporter(_writeLine)
            };

            await using var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
            WriteStatus(index.Drives.Single());
            var driveLetter = char.ToUpperInvariant(letter[0]);
            switch (parsed.Mode)
            {
                case ProgramMode.Watch:
                    await WatchDriveAsync(index, driveLetter, parsed.Seconds, cancellationToken).ConfigureAwait(false);
                    break;
                case ProgramMode.Rescan:
                    await index.RescanAsync(driveLetter, cancellationToken).ConfigureAwait(false);
                    WriteStatus(index.Drives.Single());
                    break;
                case ProgramMode.Journal:
                    await WriteJournalAsync(session, index, driveLetter, parsed, cancellationToken).ConfigureAwait(false);
                    break;
            }

            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }
}
