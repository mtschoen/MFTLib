using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// The FileIndex verbs: one dispatcher, one opener, thin verbs in the sibling DriveScanner.Index*.cs files.
partial class DriveScanner
{
    const int DefaultLimit = 20;

    /// <summary>An opened index and the broker session behind it, disposed in that order and only once.</summary>
    internal sealed class OpenedIndex(FileIndex index, BrokerSession? session) : IAsyncDisposable
    {
        bool _disposed;

        internal FileIndex Index { get; } = index;

        internal BrokerSession? Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await Index.DisposeAsync().ConfigureAwait(false);
                if (Session is not null)
                {
                    await Session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    sealed class Echo<T>(Func<T, string> format, Action<string> writeLine) : IProgress<T>
    {
        public void Report(T value)
        {
            writeLine(format(value));
        }
    }

    int RunIndexVerb(IndexVerbArguments verb, string[] commandLine)
    {
        var launchesBroker = verb.Verb == "journal"
            ? verb.Has("--maximum-size")
            : verb.Verb is not ("cache" or "elevation-status") && (verb.Text("--source") ?? "broker") == "broker";
        if (launchesBroker && !_isElevated())
        {
            // The broker raises a UAC prompt, so an attended run gets the same heads-up dialog scan-drive shows.
            if (IsUnattended())
            {
                return SkipElevationUnattended(commandLine);
            }

            if (!ConfirmElevation(commandLine, BrokerLaunchReason))
            {
                PrintElevationFailure(commandLine);
                return 1;
            }
        }

        try
        {
            // The console entry point has no synchronization context, so blocking here cannot deadlock.
            ExecuteIndexVerbAsync(verb).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _writeLine($"Error in {verb.Verb}: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }

        _writeLine($"Completed at {DateTime.Now}");
        return 0;
    }

    async Task ExecuteIndexVerbAsync(IndexVerbArguments verb)
    {
        switch (verb.Verb)
        {
            case "cache":
                InspectOrClearCache(verb);
                return;
            case "elevation-status":
                ShowElevationStatus();
                return;
        }

        if (verb.Has("--maximum-size") && verb.Drives.Count != 1)
        {
            throw new ArgumentException("Growing the journal needs exactly one drive letter; it never defaults to a drive.");
        }

        var isJournal = verb.Verb == "journal";
        var opened = await OpenIndexAsync(verb, CancellationToken.None, queryOnly: isJournal).ConfigureAwait(false);
        await using var ownedIndex = opened.ConfigureAwait(false);
        var index = opened.Index;
        switch (verb.Verb)
        {
            case "search": Search(index, verb); break;
            case "tree": ShowTree(index, verb); break;
            case "open": OpenEntry(index, verb); break;
            case "largest":
                Show(index.Largest((int)(verb.Number("--count") ?? 10), UnderOf(index, verb), CancellationToken.None), verb);
                break;
            case "duplicate-names":
                foreach (var group in index.DuplicateNames(CancellationToken.None).Take(Limit(verb)))
                {
                    _writeLine($"{group.Name}: {string.Join(" | ", group.Entries.Select(entry => entry.Path))}");
                }

                break;
            case "rescan": await RescanAsync(index, verb).ConfigureAwait(false); break;
            case "watch": await WatchAsync(opened, verb).ConfigureAwait(false); break;
            default: await JournalAsync(opened, verb).ConfigureAwait(false); break;
        }
    }

    // The one opener: --source picks the producer, the other options map one to one onto FileIndexOptions.
    async Task<OpenedIndex> OpenIndexAsync(IndexVerbArguments verb, CancellationToken cancellationToken, bool queryOnly = false)
    {
        if (verb.Text("--diagnostics") is { } logDirectory)
        {
            BrokerDiagnostics.LogDirectory = logDirectory;
            BrokerDiagnostics.Enable("client");
            _writeLine($"Broker diagnostics log directory {BrokerDiagnostics.LogDirectory}");
        }

        var source = verb.Text("--source") ?? "broker";
        var root = verb.Text("--root");
        if (source == "enumeration" && root is null || source is not ("broker" or "enumeration" or "unavailable"))
        {
            throw new ArgumentException("--source is broker, unavailable, or enumeration with --root DIRECTORY.");
        }

        if (root is not null && verb.OpenedDrives.Count != 1)
        {
            throw new ArgumentException("--root supports only one drive.");
        }

        var session = !queryOnly && source == "broker" || verb.Verb == "journal" && verb.Has("--maximum-size")
            ? CreateSession(verb)
            : null;
        try
        {
            var scan = new BrokerScanOptions
            {
                Profile = verb.Text("--profile") == "directories" ? BrokerScanProfile.DirectoryIndex : BrokerScanProfile.Full,
                KeepFileNames = verb.Text("--keep-name")?.Split(',')
            };
            var options = new FileIndexOptions
            {
                Drives = root is null
                    ? verb.OpenedDrives.Select(letter => _resolveDrive(letter.ToString())).ToArray()
                    : [new IndexedDrive(verb.OpenedDrives[0], root, 1)],
                ProducerPolicy = source == "enumeration" ? ProducerPolicy.Enumeration : ProducerPolicy.Mft,
                MftSource = source == "unavailable" || queryOnly && session is null
                    ? MftIndexSource.Unavailable("the sample scans nothing")
                    : session?.CreateIndexSource(scan),
                NoCache = queryOnly || verb.Has("--no-cache"),
                InitialOpenCacheOnly = queryOnly || verb.Has("--cache-only"),
                CacheTag = verb.Text("--cache-tag") is { } tag ? ParseCacheTag(tag) : default,
                CacheDirectory = verb.Text("--cache-directory") ?? _cacheDirectory,
                Diagnostics = verb.Has("--diagnostics") ? line => _writeLine($"  diagnostics: {line}") : null,
                OpenProgress = new Echo<IndexDriveOpened>(
                    opened => $"  opened {opened.DriveLetter}: {opened.SettledCount} of {opened.Total}", _writeLine),
                Progress = new Echo<IndexScanProgress>(
                    scanned => $"  scan {scanned.DriveLetter} {scanned.Phase}: {scanned.RowsWritten}/{scanned.TotalRows} rows in " +
                               $"{scanned.CurrentDirectory} {scanned.Outcome}", _writeLine)
            };
            _writeLine($"Opening {source}: policy {options.ProducerPolicy}, cache tag {options.CacheTag}, " +
                       $"no cache {options.NoCache}, cache only {options.InitialOpenCacheOnly}, profile {scan.Profile}, " +
                       string.Join(", ", options.Drives.Select(
                           drive => $"{drive.DriveLetter}: {drive.RootDirectory} serial {drive.VolumeSerial}")));
            var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
            index.Drives.ToList().ForEach(status => _writeLine(Describe(status)));
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

    BrokerSession CreateSession(IndexVerbArguments verb)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The broker is Windows only.");
        }

        var session = verb.Number("--connection-timeout") is { } seconds
            ? new BrokerSession(brokerArguments => OperatingSystem.IsWindows() && BrokerLauncher.Launch(brokerArguments),
                TimeSpan.FromSeconds(seconds))
            : _createBrokerSession();
        session.Connecting += () => _writeLine("  broker connecting");
        session.Connected += () => _writeLine("  broker connected");
        return session;
    }

    static CacheTag ParseCacheTag(string text)
    {
        var parts = text.Split(':');
        return new CacheTag(parts[0], uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    static int Limit(IndexVerbArguments verb)
    {
        return (int)(verb.Number("--limit") ?? DefaultLimit);
    }
}
