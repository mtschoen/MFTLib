namespace MFTLib.Index;

sealed partial class BlockWriter
{
    internal WriterHooks Hooks { get; } = new();

    /// <summary>Per-writer test hooks for row capture and racing disposal.</summary>
    internal sealed class WriterHooks
    {
        internal Action? _rowCaptured;
        internal Action? _completeAccessTaken;
    }
}
