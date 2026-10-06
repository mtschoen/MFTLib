using MFTLib;

namespace TestProgram;

// The FileIndex verbs: one dispatcher that gates elevation the way scan-drive does, then runs the verb.
// Each verb group lives in its own DriveScanner.Index*.cs file.
partial class DriveScanner
{
    int RunIndexVerb(IndexVerbArguments verb, string[] commandLine)
    {
        if (VerbLaunchesBroker(verb) && !_isElevated())
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

        using var cancellation = verb.Number("--timeout-seconds") is { } seconds
            ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds))
            : new CancellationTokenSource();
        try
        {
            // The console entry point has no synchronization context, so blocking here cannot deadlock.
            ExecuteIndexVerbAsync(verb, commandLine, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _writeLine($"{verb.Verb} stopped: the {verb.Number("--timeout-seconds")} second limit ended it.");
            return 1;
        }
        catch (Exception exception)
        {
            _writeLine($"Error in {verb.Verb}: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }

        _writeLine($"Completed at {DateTime.Now}");
        return 0;
    }

    static bool VerbLaunchesBroker(IndexVerbArguments verb)
    {
        return verb.Verb == "journal-grow" ||
               (IndexVerbSpecifications.Find(verb.Verb)!.Options.Any(option => option.Name == "--source") &&
                verb.Source == IndexVerbArguments.BrokerSource);
    }

    async Task ExecuteIndexVerbAsync(IndexVerbArguments verb, string[] commandLine, CancellationToken cancellationToken)
    {
        switch (verb.Verb)
        {
            case "cache-inspect":
                InspectCache(verb);
                return;
            case "cache-clear":
                ClearCache(verb);
                return;
            case "elevation-status":
                ShowElevationStatus(verb, commandLine);
                return;
            case "journal-grow":
                await GrowJournalThroughBrokerAsync(verb, cancellationToken).ConfigureAwait(false);
                return;
        }

        var opened = await OpenIndexAsync(verb, cancellationToken).ConfigureAwait(false);
        await using var ownedIndex = opened.ConfigureAwait(false);
        switch (verb.Verb)
        {
            case "search":
                SearchIndex(opened.Index, verb, cancellationToken);
                break;
            case "enumerate":
                EnumerateIndex(opened.Index, verb, cancellationToken);
                break;
            case "find-path":
                FindPathInIndex(opened.Index, verb, cancellationToken);
                break;
            case "tree":
                ShowTree(opened.Index, verb, cancellationToken);
                break;
            case "open":
                OpenEntry(opened.Index, verb, cancellationToken);
                break;
            case "largest":
                ShowLargest(opened.Index, verb, cancellationToken);
                break;
            case "duplicate-names":
                ShowDuplicateNames(opened.Index, verb, cancellationToken);
                break;
            case "rescan":
                await RescanAsync(opened.Index, verb, cancellationToken).ConfigureAwait(false);
                break;
            case "watch":
                await WatchAsync(opened, verb, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await ShowJournalStatusAsync(opened.Index, verb, cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
