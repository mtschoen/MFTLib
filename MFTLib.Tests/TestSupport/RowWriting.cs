using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>Drives the streaming row writer from materialized record batches, as a script or benchmark does.</summary>
static class RowWriting
{
    /// <summary>The batches as a slice source, as a script supplies them to a section writer.</summary>
    internal static IMftSliceSource Source(IEnumerable<IReadOnlyList<MftRecord>> batches) =>
        new MftRecordSliceSource(_ => batches);

    /// <summary>Writes <paramref name="batches" /> through <see cref="MftBlockRowWriter.Write" /> with no row validation.</summary>
    internal static BlockWriteResult WriteBatches(BlockWriter writer, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftScanOptions scanOptions, CancellationToken cancellationToken) =>
        WriteBatches(writer, batches, scanOptions, MftRowValidation.None, null, cancellationToken);

    /// <summary>Writes <paramref name="batches" /> with the given validation and operation reporter.</summary>
    internal static BlockWriteResult WriteBatches(BlockWriter writer, IEnumerable<IReadOnlyList<MftRecord>> batches,
        MftScanOptions scanOptions, MftRowValidation validation, ISourceOperationReporter? operation,
        CancellationToken cancellationToken)
    {
        using var source = new MftRecordSliceSource(_ => batches, validation);
        return MftBlockRowWriter.Write(writer, source, scanOptions, operation, cancellationToken);
    }
}
