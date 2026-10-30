namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        WatchState _notedWatchState;
        long _watchStateVersion;

        void NoteWatchStateLocked(WatchFault? cause = null)
        {
            var state = GetWatchStateLocked();
            if (state == _notedWatchState)
            {
                return;
            }
            _notedWatchState = state;
            _watchStateVersion++;
            foreach (var view in _views)
            {
                view.QueueWatchStateLocked(new WatchStateChange(Volume, DescribeWatchLocked(view), cause));
            }
        }

        public void RaiseWatchStateChanged() => ForEachView(view => view.RaiseWatchStateChanged());


    }
}
