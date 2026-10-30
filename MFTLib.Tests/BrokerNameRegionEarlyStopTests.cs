using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The broker host writes a scan into the client's section through the same row writer as the direct
///     producers, so a section whose name pool is exhausted stops the scan for the client's growth retry.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class BrokerNameRegionEarlyStopTests : IDisposable
{
    const int BatchCount = 30;
    const int NamesPerBatch = 100;

    readonly TempDirectory _directory = new("mftlib-broker-early-stop");

    public void Dispose() => _directory.Dispose();

    [TestMethod]
    public void Write_WhenTheSectionPoolRunsOut_StopsRequestingBatches()
    {
        var sectionName = NamedBlockSection.BuildSectionName('T');
        var options = TestBlocks.Options(Path.Combine(_directory.Path, "T-0BADF00D.mlix"), ProducerKind.Mft,
            slotCapacity: 4096, namePoolCapacity: 4096, rootRow: 5, deleteOnClose: true);
        var (creator, lifetime) = NamedBlockSection.Create(options, sectionName);
        var batchesRequested = 0;

        IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            for (var batch = 0; batch < BatchCount; batch++)
            {
                batchesRequested++;
                yield return Enumerable.Range(0, NamesPerBatch)
                    .Select(offset => new MftRecord((ulong)(6 + batch * NamesPerBatch + offset), 5,
                        new MftRecordFields(1), new string('n', 200) + (batch * NamesPerBatch + offset)))
                    .ToList();
            }
        }

        using (lifetime)
        using (creator)
        {
            var result = new RealBlockSectionWriter(TimeProvider.System).Write(sectionName, default, Batches(),
                new MftScanOptions(), default, CancellationToken.None);

            Assert.AreEqual(1, batchesRequested);
            Assert.IsGreaterThan(0L, result.SkippedRecordCount);
            Assert.IsTrue(creator.Header.IsCapacityExhausted);
        }
    }
}
