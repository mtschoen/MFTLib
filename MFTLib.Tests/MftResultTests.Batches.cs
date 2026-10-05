using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class MftResultTests
{
    [TestMethod]
    public void MftVolume_EnsureCompatibleNativeAbi_ThrowsOnMismatch()
    {
        MFTLibNative._getMftNativeAbiVersion = () => 999;

        var ex = Assert.ThrowsException<InvalidOperationException>(MFTLibNative.EnsureCompatibleNativeAbi);
        Assert.IsTrue(ex.Message.Contains("ABI mismatch"));
    }

    [TestMethod]
    public void MaterializeBatches_DefaultBatchSize_BatchesHaveExpectedLengthsAndOrder()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256, IntPtr.Zero, null);
        using var result = new MftResult(resultPtr, string.Empty);

        var expectedRecordNumbers = result.Select(r => r.RecordNumber).ToArray();
        var batches = result.MaterializeBatches().ToList();

        foreach (var batch in batches)
        {
            Assert.IsTrue(batch.Length > 0 && batch.Length <= 4096);
        }

        var actualRecordNumbers = batches.SelectMany(b => b).Select(r => r.RecordNumber).ToArray();
        CollectionAssert.AreEqual(expectedRecordNumbers, actualRecordNumbers);
    }

    [TestMethod]
    public void MaterializeBatches_CustomBatchSize_BatchesMatchRecordsInOrder()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256, IntPtr.Zero, null);
        using var result = new MftResult(resultPtr, string.Empty);

        const int batchSize = 64;
        var batches = result.MaterializeBatches(batchSize).ToList();

        Assert.IsTrue(batches.Count > 1);
        for (var i = 0; i < batches.Count - 1; i++)
        {
            Assert.AreEqual(batchSize, batches[i].Length);
        }

        Assert.IsTrue(batches[^1].Length <= batchSize);

        var expectedRecordNumbers = result.Select(r => r.RecordNumber).ToArray();
        var actualRecordNumbers = batches.SelectMany(b => b).Select(r => r.RecordNumber).ToArray();
        CollectionAssert.AreEqual(expectedRecordNumbers, actualRecordNumbers);
    }

    [TestMethod]
    public void MaterializeBatches_WithPaths_MaterializesFullPaths()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.ResolvePaths, 256, IntPtr.Zero, null);
        using var result = new MftResult(resultPtr, "C");

        var batches = result.MaterializeBatches(50).ToList();
        var withPaths = batches.SelectMany(b => b).Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0);
    }

    [TestMethod]
    public void MaterializeBatches_RecordsStayValidAfterNextBatch()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256, IntPtr.Zero, null);
        using var result = new MftResult(resultPtr, string.Empty);

        using var enumerator = result.MaterializeBatches(10).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        var firstBatch = enumerator.Current;
        var firstBatchNames = firstBatch.Select(r => r.FileName).ToArray();

        Assert.IsTrue(enumerator.MoveNext());
        var secondBatch = enumerator.Current;
        Assert.IsTrue(secondBatch.Length > 0);

        for (var i = 0; i < firstBatch.Length; i++)
        {
            Assert.AreEqual(firstBatchNames[i], firstBatch[i].FileName);
        }
    }

    [TestMethod]
    public void ToArray_MatchesMaterializeBatches()
    {
        Assert.IsNotNull(_tempMftPath);
        MFTLibNative.EnsureCompatibleNativeAbi();
        var resultPtr = MFTLibNative._parseMftFromFile(_tempMftPath, null, MatchFlags.None, 256, IntPtr.Zero, null);
        using var result = new MftResult(resultPtr, string.Empty);
        var expectedRecords = result.ToArray();

        var batches = result.MaterializeBatches(128).ToList();
        var actualRecords = batches.SelectMany(b => b).ToArray();

        Assert.AreEqual(expectedRecords.Length, actualRecords.Length);
        for (var i = 0; i < expectedRecords.Length; i++)
        {
            Assert.AreEqual(expectedRecords[i].RecordNumber, actualRecords[i].RecordNumber);
            Assert.AreEqual(expectedRecords[i].FileName, actualRecords[i].FileName);
        }
    }
}
