namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class IndexView
    {
        internal bool DetachRegistry() => Scan.Key is not { } key || Registry.Detach(key, Scan);

        internal async Task DetachAsync()
        {
            Scan.RemoveView(this);
            if (DetachRegistry())
            {
                await Scan.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
