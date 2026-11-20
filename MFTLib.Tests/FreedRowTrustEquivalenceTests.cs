using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The memoized freed-row trust pass decides every freed row exactly as the per-row parent-chain walk it
///     replaced. Each scenario writes generated records through the real row writer, then replays the replaced walk,
///     kept here as <see cref="ReferenceTrust" />, over the same rows and compares the verdict of every freed row.
///     A failure names its shape and seed, which <see cref="RunScenario" /> replays alone.
/// </summary>
[TestClass]
public sealed class FreedRowTrustEquivalenceTests : IDisposable
{
    const uint RootRecord = 5;
    const int RandomSeedCount = 150;
    static readonly MftScanOptions IncludingFreed = new() { IncludeFreed = true };
    TempDirectory _directory = null!;

    public enum Shape
    {
        RandomGraph,
        DeepChain,
        LongNames
    }

    [TestInitialize]
    public void Initialize() => _directory = new TempDirectory("mftlib-freed-trust");

    [TestCleanup]
    public void Cleanup() => _directory.Dispose();

    public void Dispose() => Cleanup();

    [TestMethod]
    public void RandomParentGraphs_MatchTheReferenceWalk()
    {
        var totals = new VerdictTotals();
        for (var seed = 0; seed < RandomSeedCount; seed++)
        {
            totals.Add(RunScenario(Shape.RandomGraph, seed));
        }

        totals.AssertBothOutcomes(Shape.RandomGraph);
    }

    [TestMethod]
    [DataRow(Shape.DeepChain, 1)]
    [DataRow(Shape.DeepChain, 2)]
    [DataRow(Shape.DeepChain, 3)]
    [DataRow(Shape.LongNames, 1)]
    [DataRow(Shape.LongNames, 2)]
    [DataRow(Shape.LongNames, 3)]
    [DataRow(Shape.LongNames, 4)]
    public void AdversarialChains_MatchTheReferenceWalk(Shape shape, int seed)
    {
        var totals = new VerdictTotals();
        totals.Add(RunScenario(shape, seed));
        totals.AssertBothOutcomes(shape);
    }

    /// <summary>Generates one scenario, writes it, and asserts every freed row's verdict against the reference.</summary>
    VerdictTotals RunScenario(Shape shape, int seed)
    {
        var scenario = shape switch
        {
            Shape.RandomGraph => FreedTrustScenarios.RandomGraph(seed),
            Shape.DeepChain => FreedTrustScenarios.DeepChain(seed),
            _ => FreedTrustScenarios.LongNames(seed)
        };
        var context = $"{shape} seed {seed}";
        var path = Path.Combine(_directory.Path, $"{shape}-{seed}.bin");
        using var block = TestBlocks.Create(path, ProducerKind.Mft, slotCapacity: scenario.SlotCapacity,
            namePoolCapacity: scenario.NamePoolCapacity, volumeSerial: 123, rootRow: RootRecord);

        var result = RowWriting.WriteBatches(new BlockWriter(block), scenario.Batches, IncludingFreed,
            CancellationToken.None);

        Assert.AreEqual(scenario.ExpectedSkippedCount, result.SkippedRecordCount, $"{context}: skipped records");
        var reference = new ReferenceTrust(block, scenario);
        var totals = new VerdictTotals();
        foreach (var record in scenario.Records)
        {
            if (!FreedTrustScenario.IsWritten(record))
            {
                continue;
            }

            var row = (uint)record.RecordNumber;
            var storedParent = block.Rows[(int)row].ParentRow;
            if (record.InUse)
            {
                Assert.AreEqual((uint)record.ParentRecordNumber, storedParent,
                    $"{context}: live row {row} is never detached");
                continue;
            }

            var expected = reference.IsTrusted(row);
            Assert.AreEqual(expected ? (uint)record.ParentRecordNumber : BlockLayout.DetachedParentRow, storedParent,
                $"{context}: freed row {row}");
            totals.Count(expected);
        }

        return totals;
    }

    sealed class VerdictTotals
    {
        int _trusted;
        int _untrusted;

        public void Count(bool trusted)
        {
            if (trusted)
            {
                _trusted++;
            }
            else
            {
                _untrusted++;
            }
        }

        public void Add(VerdictTotals other)
        {
            _trusted += other._trusted;
            _untrusted += other._untrusted;
        }

        // A generator whose rows all land on one side would compare nothing that matters.
        public void AssertBothOutcomes(Shape shape)
        {
            Assert.IsTrue(_trusted > 0, $"{shape}: the scenarios trust some freed row");
            Assert.IsTrue(_untrusted > 0, $"{shape}: the scenarios detach some freed row");
        }
    }

    /// <summary>
    ///     The per-row walk the memoized pass replaced, unchanged except that it reads each row's parent from the
    ///     generated records, since the pass under test has already detached the untrusted rows in the block.
    /// </summary>
    sealed class ReferenceTrust(BlockFile block, FreedTrustScenario scenario)
    {
        readonly record struct RecordFacts(bool IsDirectory, bool IsFreed, ushort Sequence);

        public bool IsTrusted(uint origin)
        {
            // A freed root record is no root: nothing above it verifies it.
            if (origin == RootRecord)
            {
                return false;
            }

            var visited = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
            long units = 0;
            var current = origin;
            var rowCount = block.RowCount;
            while (current != RootRecord && current < rowCount && visited.Count < BlockLayout.MaximumPathDepth)
            {
                if (visited.Contains(current))
                {
                    break;
                }

                visited.Add(current);
                var nameLength = NamePool.ReadRowName(block, current).Length;
                if (nameLength == 0)
                {
                    break;
                }

                units += nameLength;
                var parent = scenario.ParentRowOf(current);
                if (!TrustsParentHop(current, parent))
                {
                    return false;
                }

                current = parent;
            }

            // The separators between the components are part of the path.
            return current == RootRecord && units + visited.Count - 1 <= BlockLayout.MaximumPathUnits;
        }

        bool TrustsParentHop(uint child, uint parent)
        {
            if (!TryGetFacts(parent, out var facts) || !facts.IsDirectory)
            {
                return false;
            }

            var referencedSequence = scenario.ParentSequenceOf(child);
            return facts.Sequence == referencedSequence ||
                   (facts.IsFreed && facts.Sequence == (ushort)(referencedSequence + 1));
        }

        bool TryGetFacts(uint record, out RecordFacts facts)
        {
            if (record < block.RowCount)
            {
                ref readonly var row = ref block.Rows[(int)record];
                if (row.IsInUse)
                {
                    facts = new RecordFacts(row.IsDirectory, row.IsDeleted, block.SequenceNumbers[(int)record]);
                    return true;
                }
            }

            if (record == RootRecord && scenario.ObservedRoot is { } root)
            {
                facts = new RecordFacts(root.IsDirectory, !root.InUse, root.SequenceNumber);
                return true;
            }

            facts = default;
            return false;
        }
    }
}
