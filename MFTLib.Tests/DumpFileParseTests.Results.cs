using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The result of a real dump parse: its counts, and records that stay valid however they are copied out.
public partial class DumpFileParseTests
{
    [TestMethod]
    public void Result_TotalRecords_IsTheFilesRecordCount()
    {
        DirectParse.ParseFile(_syntheticPath, out _, out var totalRecords);

        Assert.AreEqual(1000UL, totalRecords);
    }

    [TestMethod]
    public void Result_UsedRecords_IsAtMostTheTotal()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out _, out var totalRecords);

        Assert.IsTrue((ulong)records.Length <= totalRecords);
    }

    [TestMethod]
    public void ToArray_MaterializesRecords_StableStrings()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out _);

        foreach (var record in records)
        {
            var name = record.FileName;
            Assert.IsNotNull(name);
            Assert.AreEqual(name, record.FileName);
        }
    }

    [TestMethod]
    public void MaterializeBatches_DefaultBatchSize_BatchesHaveExpectedLengthsAndOrder()
    {
        using var result = StreamFile();

        var expectedRecordNumbers = result.Select(record => record.RecordNumber).ToArray();
        var batches = result.MaterializeBatches().ToList();

        Assert.IsTrue(batches.All(batch => batch.Length is > 0 and <= 4096));
        CollectionAssert.AreEqual(expectedRecordNumbers,
            batches.SelectMany(batch => batch).Select(record => record.RecordNumber).ToArray());
    }

    [TestMethod]
    public void MaterializeBatches_CustomBatchSize_BatchesMatchRecordsInOrder()
    {
        using var result = StreamFile();
        const int batchSize = 64;

        var batches = result.MaterializeBatches(batchSize).ToList();

        Assert.IsTrue(batches.Count > 1);
        Assert.IsTrue(batches[..^1].All(batch => batch.Length == batchSize));
        Assert.IsTrue(batches[^1].Length <= batchSize);
        CollectionAssert.AreEqual(result.Select(record => record.RecordNumber).ToArray(),
            batches.SelectMany(batch => batch).Select(record => record.RecordNumber).ToArray());
    }

    [TestMethod]
    public void MaterializeBatches_RecordsStayValidAfterNextBatch()
    {
        using var result = StreamFile();

        using var enumerator = result.MaterializeBatches(10).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        var firstBatch = enumerator.Current;
        var firstBatchNames = firstBatch.Select(record => record.FileName).ToArray();
        Assert.IsTrue(enumerator.MoveNext());
        Assert.IsTrue(enumerator.Current.Length > 0);

        CollectionAssert.AreEqual(firstBatchNames, firstBatch.Select(record => record.FileName).ToArray());
    }

    [TestMethod]
    public void ToArray_MatchesMaterializeBatches()
    {
        using var result = StreamFile();
        var expectedRecords = result.ToArray();

        var actualRecords = result.MaterializeBatches(128).SelectMany(batch => batch).ToArray();

        CollectionAssert.AreEqual(expectedRecords.Select(record => record.RecordNumber).ToArray(),
            actualRecords.Select(record => record.RecordNumber).ToArray());
        CollectionAssert.AreEqual(expectedRecords.Select(record => record.FileName).ToArray(),
            actualRecords.Select(record => record.FileName).ToArray());
    }
}
