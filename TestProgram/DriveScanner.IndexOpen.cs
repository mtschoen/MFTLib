using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// The one opener every index verb shares: it turns the opener options into FileIndexOptions, opens the
// index and prints every drive's status. Sources: the broker (the production path), a directory
// enumeration (no elevation, no volume), or an unavailable source that only warm-starts from cache.
partial class DriveScanner
{
    const string SampleDiagnosticsRole = "client";
    const string UnavailableDefaultReason = "the sample was told to scan nothing";
    const uint DefaultEnumerationVolumeSerial = 1;

    /// <summary>An opened index and the broker session behind it, disposed in that order and only once.</summary>
    internal sealed class OpenedIndex(FileIndex index, BrokerSession? session) : IAsyncDisposable
    {
        bool _disposed;

        internal FileIndex Index { get; } = index;

        internal BrokerSession? Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Index.DisposeAsync().ConfigureAwait(false);
            if (Session is not null)
            {
                await Session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    async Task<OpenedIndex> OpenIndexAsync(IndexVerbArguments verb, CancellationToken cancellationToken,
        bool queryOnly = false)
    {
        ConfigureBrokerDiagnostics(verb);
        var session = verb.Source == IndexVerbArguments.BrokerSource ? CreateIndexSession(verb) : null;
        try
        {
            var options = BuildIndexOptions(verb, session, queryOnly);
            PrintIndexOptions(options, verb);
            var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
            foreach (var status in index.Drives)
            {
                PrintDriveStatus(status);
            }

            return new OpenedIndex(index, session);
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    void ConfigureBrokerDiagnostics(IndexVerbArguments verb)
    {
        if (verb.Text("--diagnostics") is not { } directory)
        {
            return;
        }

        BrokerDiagnostics.LogDirectory = directory;
        BrokerDiagnostics.Enable(SampleDiagnosticsRole);
        _writeLine($"Broker diagnostics enabled; the log directory reads back as {BrokerDiagnostics.LogDirectory}");
    }

    BrokerSession CreateIndexSession(IndexVerbArguments verb)
    {
        BrokerSession session;
        if (verb.Number("--connection-timeout") is { } seconds)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The broker is Windows only.");
            }

            session = new BrokerSession(LaunchBroker, TimeSpan.FromSeconds(seconds));
            _writeLine($"Broker session with a launch callback and a {seconds} second connection timeout.");
        }
        else
        {
            session = _createBrokerSession();
        }

        session.Connecting += () => _writeLine("  broker: connecting");
        session.Connected += () => _writeLine("  broker: connected");
        return session;
    }

    bool LaunchBroker(string brokerArguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The broker is Windows only.");
        }

        _writeLine($"  broker: launching elevated with arguments {brokerArguments}");
        return BrokerLauncher.Launch(brokerArguments);
    }

    FileIndexOptions BuildIndexOptions(IndexVerbArguments verb, BrokerSession? session, bool queryOnly)
    {
        var enumeration = verb.Source == IndexVerbArguments.EnumerationSource;
        return new FileIndexOptions
        {
            Drives = BuildIndexedDrives(verb),
            ProducerPolicy = enumeration ? ProducerPolicy.Enumeration : ProducerPolicy.Mft,
            MftSource = BuildMftSource(verb, session),
            NoCache = queryOnly || verb.Has("--no-cache"),
            InitialOpenCacheOnly = queryOnly || verb.Has("--cache-only"),
            CacheTag = verb.Tag() ?? default,
            CacheDirectory = verb.Text("--cache-directory") ?? _cacheDirectory,
            Diagnostics = verb.Has("--diagnostics") ? line => _writeLine($"  index diagnostics: {line}") : null,
            OpenProgress = new DriveOpenedPrinter(_writeLine),
            Progress = new ScanProgressPrinter(_writeLine)
        };
    }

    IndexedDrive[] BuildIndexedDrives(IndexVerbArguments verb)
    {
        if (verb.Text("--root") is { } root)
        {
            var serial = (uint)(verb.Number("--volume-serial") ?? DefaultEnumerationVolumeSerial);
            return [new IndexedDrive(verb.Drives[0], root, serial)];
        }

        return verb.Drives.Select(letter => _resolveDrive(letter.ToString())).ToArray();
    }

    static MftIndexSource? BuildMftSource(IndexVerbArguments verb, BrokerSession? session)
    {
        if (verb.Source == IndexVerbArguments.UnavailableSource)
        {
            return MftIndexSource.Unavailable(verb.Text("--unavailable-reason") ?? UnavailableDefaultReason);
        }

        if (session is null)
        {
            return null;
        }

        var scanOptions = new BrokerScanOptions
        {
            Profile = verb.Text("--profile") == IndexVerbArguments.DirectoriesProfile
                ? BrokerScanProfile.DirectoryIndex
                : BrokerScanProfile.Full,
            KeepFileNames = verb.Has("--keep-name") ? verb.TextList("--keep-name") : null
        };
        return session.CreateIndexSource(scanOptions);
    }

    void PrintIndexOptions(FileIndexOptions options, IndexVerbArguments verb)
    {
        _writeLine($"Opening an index from source {verb.Source}; producer policy {options.ProducerPolicy}; " +
                   $"no cache {options.NoCache}; initial open cache only {options.InitialOpenCacheOnly}; " +
                   $"cache tag {options.CacheTag}; cache directory {options.CacheDirectory ?? "(library default)"}");
        foreach (var drive in options.Drives)
        {
            _writeLine($"  drive {drive.DriveLetter}: root {drive.RootDirectory} volume serial {drive.VolumeSerial}");
        }

        if (verb.Source == IndexVerbArguments.BrokerSource)
        {
            _writeLine($"  broker profile {verb.Text("--profile") ?? IndexVerbArguments.FullProfile}; keep names " +
                       $"[{string.Join(", ", verb.TextList("--keep-name"))}]");
        }
    }

    void PrintDriveStatus(DriveStatus status)
    {
        _writeLine($"Drive {status.DriveLetter}: state {status.State}; failure {status.FailureKind}; block source " +
                   $"{status.BlockSource}; cache slot {status.CacheSlot}; scan timestamp {status.ScanTimestamp:u}");
        _writeLine($"  rows {status.LiveRowCount}; skipped records {status.SkippedRecordCount}; access denied " +
                   $"subtrees {status.AccessDeniedSubtreeCount}; compaction needed {status.CompactionNeeded}");
        _writeLine($"  watch supported {status.WatchSupported}; requested {status.WatchRequested}; catch-up " +
                   $"{status.WatchCatchUp}; state version {status.WatchStateVersion}; consecutive lost catch-ups " +
                   $"{status.ConsecutiveLostCatchUps} of {FileIndex.LostCatchUpRecoveryLimit}");
        _writeLine($"  producer failure {status.MftProducerFailureMessage ?? "none"}; watch failure " +
                   $"{status.WatchFailureMessage ?? "none"}");
        _writeLine(status.CheckpointLoss is { } loss
            ? $"  checkpoint loss: {FormatCheckpointLoss(loss)}"
            : "  checkpoint loss: none");
    }

    // Reports each drive that settles, on the thread that settled it.
    sealed class DriveOpenedPrinter(Action<string> writeLine) : IProgress<IndexDriveOpened>
    {
        public void Report(IndexDriveOpened value)
        {
            writeLine($"  opened drive {value.DriveLetter}: {value.SettledCount} of {value.Total} settled");
        }
    }

    // Reports each scan phase once and every outcome, with every field of the progress value.
    sealed class ScanProgressPrinter(Action<string> writeLine) : IProgress<IndexScanProgress>
    {
        IndexScanPhase? _lastPhase;

        public void Report(IndexScanProgress value)
        {
            if (_lastPhase == value.Phase && value.Outcome is null)
            {
                return;
            }

            _lastPhase = value.Phase;
            writeLine($"  scan drive {value.DriveLetter}: phase {value.Phase}; rows {value.RowsWritten} of " +
                      $"{Describe(value.TotalRows, "unknown")}; directory " +
                      $"{value.CurrentDirectory ?? "none"}; outcome {value.Outcome?.ToString() ?? "pending"}");
        }
    }
}
