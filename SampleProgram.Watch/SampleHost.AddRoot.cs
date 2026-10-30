using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    sealed record WatchRun(List<FileIndex> Owned, List<FileIndex> Ready, RootChangeCounter Counter,
        List<RootOperationResult> Operations, Func<RootKey, CancellationToken, Task<FileIndex>> OpenRoot)
    {
        internal int FailedAdds { get; set; }
    }

    async Task AddRootAsync(string text, WatchRun run, CancellationToken cancellationToken)
    {
        if (!RootKey.TryParse(text, out var root))
        {
            await _output.WriteLineAsync($"Root '{text}' is neither a drive nor a full path.").ConfigureAwait(false);
            return;
        }

        FileIndex? added = null;
        var adopted = false;
        try
        {
            added = await run.OpenRoot(root, cancellationToken).ConfigureAwait(false);
            // The set validates duplicate resolved roots before this candidate joins the owned watch list.
            _ = new FileIndexSet([.. run.Ready, added]);
            WriteRootDetail(added.Status);
            run.Owned.Add(added);
            run.Ready.Add(added);
            adopted = true;
            ReportIndexChanges(added, run.Counter);
            run.Operations.AddRange(Report("start", [await added.StartWatchingAsync(cancellationToken).ConfigureAwait(false)]));
            run.Operations.AddRange(Report("catch-up", [await added.WaitForCatchUpAsync(cancellationToken).ConfigureAwait(false)]));
            await _output.WriteLineAsync($"Added root {root}: producer {added.Status.Producer}").ConfigureAwait(false);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            run.FailedAdds++;
            await _output.WriteLineAsync($"Error adding root {root}: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            if (!adopted && added is not null)
            {
                await added.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
