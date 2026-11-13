namespace MFTLib;

/// <summary>
///     Materialized record batches as a slice source: each batch is packed into one slice, so records from a script or
///     a benchmark are written by the same writer as a native parse. The batches carry their final sizes, so the
///     completion has no extension data sizes.
/// </summary>
sealed class MftRecordSliceSource : IMftSliceSource
{
    readonly Func<Action<long>, IEnumerable<IReadOnlyList<MftRecord>>> _batches;
    long _recordsOmitted;

    /// <summary>Wraps the batches a function yields once enumeration starts.</summary>
    /// <param name="batches">
    ///     Given the callback that counts the records the source left out, yields the record batches.
    /// </param>
    /// <param name="validation">The checks the records pass, as the source they stand in for would set them.</param>
    internal MftRecordSliceSource(Func<Action<long>, IEnumerable<IReadOnlyList<MftRecord>>> batches,
        MftRowValidation validation = MftRowValidation.None)
    {
        _batches = batches;
        Validation = validation;
    }

    public MftRowValidation Validation { get; }

    /// <summary>Packs each nonempty batch into a slice as it is enumerated.</summary>
    public IEnumerable<MftParsedSlice> Slices()
    {
        foreach (var batch in _batches(count => _recordsOmitted += count))
        {
            if (batch.Count > 0)
            {
                yield return MftParsedSlice.Pack(batch);
            }
        }
    }

    /// <summary>The records the batches' source said it left out.</summary>
    public MftSliceCompletion Finish() => new(_recordsOmitted, []);

    /// <summary>Holds nothing to release: the batches end with their enumeration.</summary>
    public void Dispose()
    {
    }
}
