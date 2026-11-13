using MFTLib;

namespace Benchmark;

/// <summary>Slices packed before a measurement starts, so the clock covers only the row writer.</summary>
sealed class PackedSliceSource(MftParsedSlice[] slices) : IMftSliceSource
{
    public MftRowValidation Validation => MftRowValidation.None;

    public IEnumerable<MftParsedSlice> Slices() => slices;

    public MftSliceCompletion Finish() => new(0, []);

    public void Dispose()
    {
    }
}
