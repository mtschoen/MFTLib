using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        internal readonly record struct RoutedChanges(IndexView View, IReadOnlyList<FileChange> Changes);

        // WriteGate orders this snapshot against attach and publication. Every entry is rebound here,
        // while its receiving view still names the mapping on which this batch was applied.
        List<RoutedChanges> RouteLocked(IReadOnlyList<FileChange> changes)
        {
            var bindings = new List<(IndexView View, RootBlock Block, List<FileChange> Changes)>();
            foreach (var view in ViewsSnapshot())
            {
                if (view.Block is { } block)
                {
                    bindings.Add((view, block, []));
                }
            }
            foreach (var change in changes)
            {
                string? foldedPath = null;
                string? foldedPreviousPath = null;
                foreach (var binding in bindings)
                {
                    if (Contains(binding.Block, change, ref foldedPath, ref foldedPreviousPath))
                    {
                        binding.Changes.Add(change with { Entry = change.Entry.WithRootBlock(binding.Block) });
                    }
                }
            }
            return bindings.Select(binding => new RoutedChanges(binding.View, binding.Changes)).ToList();
        }

        static bool Contains(RootBlock block, FileChange change, ref string? foldedPath, ref string? foldedPreviousPath)
        {
            if (!block.View.IsDirectoryView)
            {
                return true;
            }
            if (LookupEngine.TryMatchRootPrefix(block.Kind, block.MatchableRootDirectoryPath, change.Path, ref foldedPath, out _))
            {
                return true;
            }
            return change.PreviousPath is { } previous &&
                LookupEngine.TryMatchRootPrefix(block.Kind, block.MatchableRootDirectoryPath, previous, ref foldedPreviousPath, out _);
        }

        internal void DeliverChanges(IReadOnlyList<RoutedChanges> routes, WatchInstance? instance)
        {
            List<Exception>? failures = null;
            foreach (var route in routes)
            {
                try
                {
                    route.View.RaiseChanged(route.Changes);
                }
                catch (Exception exception)
                {
                    if (instance is not null)
                    {
                        AnnounceSubscriberFault(instance, route.View, exception);
                    }
                    else
                    {
                        (failures ??= []).Add(exception);
                    }
                }
            }
            if (failures is { Count: 1 })
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            if (failures is not null)
            {
                throw new AggregateException(failures);
            }
        }
    }
}
