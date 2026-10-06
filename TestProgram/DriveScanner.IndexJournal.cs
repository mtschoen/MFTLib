using MFTLib.Index;

namespace TestProgram;

// The journal verbs: journal-status reads sizing and checkpoint-loss state; journal-grow is the one
// verb that changes a volume, and only to sizes the command line gives.
partial class DriveScanner
{
    async Task ShowJournalStatusAsync(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        foreach (var status in index.Drives)
        {
            PrintJournalSettings(index, status.DriveLetter);
            PrintRecoveryExplanation(status);
        }

        if (!verb.Has("--wait-catch-up"))
        {
            return;
        }

        // Waiting for catch-up is where a lost checkpoint surfaces as JournalCatchUpLostException.
        var all = new LifecycleScope(null, null);
        try
        {
            await StartWatchingAsync(index, all, cancellationToken).ConfigureAwait(false);
            await WaitForCatchUpAsync(index, all, cancellationToken).ConfigureAwait(false);
        }
        catch (JournalCatchUpLostException lost)
        {
            _writeLine($"Catch-up lost: {lost.Message}");
            _writeLine($"  recovery stopped {lost.RecoveryStopped}; {ExplainCatchUpLoss(lost)}");
        }
        finally
        {
            await StopWatchingAsync(index, all, cancellationToken).ConfigureAwait(false);
        }

        foreach (var status in index.Drives)
        {
            PrintRecoveryExplanation(status);
        }
    }

    void PrintJournalSettings(FileIndex index, char driveLetter)
    {
        if (!OperatingSystem.IsWindows())
        {
            _writeLine($"  drive {driveLetter}: journal sizing needs Windows.");
            return;
        }

        try
        {
            var settings = index.QueryUsnJournalSettings(driveLetter);
            _writeLine($"  drive {driveLetter}: {FormatSettings(settings)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _writeLine($"  drive {driveLetter}: journal sizing unavailable: {exception.GetType().Name}: {exception.Message}");
        }
    }

    async Task GrowJournalThroughBrokerAsync(IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Growing a USN journal needs Windows.");
        }

        var letter = verb.Drives[0];
        var maximumSize = verb.RequiredNumber("--maximum-size");
        var allocationDelta = verb.RequiredNumber("--allocation-delta");

        // The index exists only to read sizing before and after: it opens cache-only with no cache, so no scan runs.
        var opened = await OpenIndexAsync(verb, cancellationToken, queryOnly: true).ConfigureAwait(false);
        await using var ownedIndex = opened.ConfigureAwait(false);
        _writeLine($"Before growing drive {letter}:");
        PrintJournalSettings(opened.Index, letter);

        _writeLine($"Growing drive {letter}: to maximum size {maximumSize} bytes with allocation delta {allocationDelta} bytes.");
        var session = opened.Session ?? throw new InvalidOperationException("The broker source always opens a session.");
        var grown = await session.GrowUsnJournalAsync(letter, maximumSize, allocationDelta, cancellationToken)
            .ConfigureAwait(false);
        _writeLine($"The broker reports {FormatSettings(grown)}");

        _writeLine($"After growing drive {letter}:");
        PrintJournalSettings(opened.Index, letter);
    }
}
