namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed class OpenSettlement
    {
        int _settledDriveCount;

        // The caller holds the index's state lock at a terminal settle mutation.
        public int ClaimLocked() => ++_settledDriveCount;
    }
}
