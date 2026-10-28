namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        // I3 Views list: membership changes under StateLock. Attaches to a published mapping
        // also take WriteGate, ordering binding refresh against journal batches.
        readonly List<IndexView> _views = [];

        internal void AttachFirstView(IndexView view)
        {
            lock (StateLock)
            {
                _views.Add(view);
            }
        }

        IndexView[] ViewsSnapshot()
        {
            lock (StateLock)
            {
                return [.. _views];
            }
        }

        void ForEachView(Action<IndexView> action)
        {
            foreach (var view in ViewsSnapshot())
            {
                action(view);
            }
        }

        // I4 Publication: bindings are built under WriteGate, outside StateLock; the scan
        // and all view handles are then swapped together under StateLock.
        (IndexView View, RootBlock Block)[] BindViews(MappedBlock mapped)
        {
            var views = ViewsSnapshot();
            var bindings = new List<(IndexView, RootBlock)>(views.Length);
            foreach (var view in views)
            {
                ClosingToken.ThrowIfCancellationRequested();
                bindings.Add((view, view.BindBlock(mapped)));
            }
            return [.. bindings];
        }

        void PublishBindingsLocked(RootBlock block, (IndexView View, RootBlock Block)[] bindings)
        {
            Block = block;
            foreach (var (view, bound) in bindings)
            {
                view.Block = bound;
            }
        }

        internal void ThrowIfClosing(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsClosing)
            {
                throw new OperationCanceledException("The scan is closing.", ClosingToken);
            }
        }

        internal Task CancelClosingAsync() => _closing.CancelAsync();
    }
}
