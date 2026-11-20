using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>Generated MFT records for a freed-row trust scenario, and the facts the reference walk reads from them.</summary>
sealed class FreedTrustScenario
{
    readonly Dictionary<uint, MftRecord> _writtenRecords = [];

    public FreedTrustScenario(uint slotCapacity, uint namePoolCapacity, List<MftRecord> records, Random random)
    {
        SlotCapacity = slotCapacity;
        NamePoolCapacity = namePoolCapacity;
        Records = records;
        foreach (var record in records)
        {
            if (record.RecordNumber == FreedTrustScenarios.RootRecord)
            {
                ObservedRoot = record;
            }

            if (IsWritten(record))
            {
                _writtenRecords.Add((uint)record.RecordNumber, record);
            }
            else
            {
                ExpectedSkippedCount++;
            }
        }

        // Shuffled and split, so parents arrive before and after their children and the pass meets rows in any order.
        var shuffled = records.ToArray();
        random.Shuffle(shuffled);
        var batchCount = random.Next(1, 5);
        var batchSize = (shuffled.Length + batchCount - 1) / batchCount;
        Batches = shuffled.Chunk(batchSize).ToArray();
    }

    public uint SlotCapacity { get; }

    public uint NamePoolCapacity { get; }

    public IReadOnlyList<MftRecord> Records { get; }

    public IReadOnlyList<IReadOnlyList<MftRecord>> Batches { get; }

    public long ExpectedSkippedCount { get; }

    /// <summary>The last record 5 the writer saw, whether or not it became a row.</summary>
    public MftRecord? ObservedRoot { get; }

    /// <summary>Whether the row writer writes the record, as opposed to skipping and counting it.</summary>
    public static bool IsWritten(MftRecord record) =>
        record.ParentRecordNumber <= uint.MaxValue &&
        (record.FileName.Length > 0 || record.RecordNumber == FreedTrustScenarios.RootRecord);

    /// <summary>The parent row the writer stored for a written row, before any detach.</summary>
    public uint ParentRowOf(uint row) => (uint)_writtenRecords[row].ParentRecordNumber;

    public ushort ParentSequenceOf(uint row) => _writtenRecords[row].ParentSequenceNumber;
}

/// <summary>Builds random and adversarial parent graphs for freed-row trust, each fully determined by its seed.</summary>
static class FreedTrustScenarios
{
    public const uint RootRecord = 5;
    const ushort RootSequence = 5;
    const uint ChainStart = 20;

    /// <summary>
    ///     A small random graph: live, freed, directory and file records under the root, under each other (branches and
    ///     cycles), under themselves, under missing records and past the last row, with parent sequences that match,
    ///     match one lower, or miss; the root is live, freed, a file, absent, rowless or nameless.
    /// </summary>
    public static FreedTrustScenario RandomGraph(int seed)
    {
        var random = new Random(seed);
        var slotCapacity = (uint)random.Next(16, 200);
        var candidates = Enumerable.Range(0, (int)slotCapacity).Select(number => (uint)number)
            .Where(number => number != RootRecord).ToArray();
        random.Shuffle(candidates);
        var numbers = candidates.Take(random.Next(4, candidates.Length)).ToArray();
        var sequences = numbers.ToDictionary(number => number,
            _ => random.Next(10) == 0 ? ushort.MaxValue : (ushort)random.Next(4));
        var records = new List<MftRecord>();
        if (RandomRoot(random) is { } root)
        {
            records.Add(root);
            sequences[RootRecord] = root.SequenceNumber;
        }

        foreach (var number in numbers)
        {
            var parent = RandomParent(random, number, numbers, slotCapacity);
            var parentSequence = RandomParentSequence(random,
                sequences.TryGetValue((uint)parent, out var stored) ? stored : (ushort)random.Next(4));
            var name = random.Next(30) == 0 ? "" : new string((char)('a' + random.Next(3)), random.Next(1, 4));
            records.Add(Record(number, parent, name, sequences[number], parentSequence,
                inUse: random.Next(2) == 0, isDirectory: random.Next(4) != 0));
        }

        return new FreedTrustScenario(slotCapacity, 4096, records, random);
    }

    /// <summary>
    ///     One chain of one-unit directories past <see cref="BlockLayout.MaximumPathDepth" />, live with some freed
    ///     links, with freed files hanging off it on both sides of the component limit, a freed branch, a freed cycle
    ///     and leaves whose parent sequence misses.
    /// </summary>
    public static FreedTrustScenario DeepChain(int seed)
    {
        var random = new Random(seed);
        var chainLength = BlockLayout.MaximumPathDepth + 10;
        var records = new List<MftRecord> { Root() };
        AddChain(random, records, chainLength, _ => "d", freedLinks: 16);
        var next = ChainStart + (uint)chainLength;
        for (var leaf = 0; leaf < 60; leaf++)
        {
            // Directory i sits at depth i + 1, so its file has i + 2 components.
            var directoryIndex = leaf < 45
                ? random.Next(BlockLayout.MaximumPathDepth - 14, chainLength)
                : random.Next(chainLength);
            AddLeaf(random, records, next++, ChainStart + (uint)directoryIndex, "f", sequenceMayMiss: true);
        }

        var branchParent = ChainStart + (uint)random.Next(BlockLayout.MaximumPathDepth - 40, chainLength);
        for (var index = 0; index < 30; index++)
        {
            records.Add(Record(next, index == 0 ? branchParent : next - 1, "b", 1, 1, inUse: false, isDirectory: true));
            AddLeaf(random, records, next + 100, next, "l", sequenceMayMiss: true);
            next++;
        }

        next += 100;
        records.Add(Record(next, next + 1, "c", 1, 1, inUse: false, isDirectory: true));
        records.Add(Record(next + 1, next, "c", 1, 1, inUse: false, isDirectory: true));
        AddLeaf(random, records, next + 2, next, "l", sequenceMayMiss: true);
        return new FreedTrustScenario(next + 3, 4096, records, random);
    }

    /// <summary>
    ///     A chain of names of 200 to 300 units that crosses <see cref="BlockLayout.MaximumPathUnits" />, with freed
    ///     files of 1 to 300 units at every depth, so the path-length limit splits them, and files that rebuild to one
    ///     unit under the limit, exactly to it and one or two units past it.
    /// </summary>
    public static FreedTrustScenario LongNames(int seed)
    {
        var random = new Random(seed);
        const int chainLength = 150;
        var names = Enumerable.Range(0, chainLength).Select(_ => new string('n', random.Next(200, 301))).ToArray();
        var records = new List<MftRecord> { Root() };
        AddChain(random, records, chainLength, index => names[index], freedLinks: 20);
        var next = ChainStart + chainLength;
        for (var leaf = 0; leaf < 200; leaf++)
        {
            AddLeaf(random, records, next++, ChainStart + (uint)random.Next(chainLength),
                new string('f', random.Next(1, 301)), sequenceMayMiss: true);
        }

        long chainUnits = 0;
        for (var index = 0; index < chainLength; index++)
        {
            // A file under directory index has index + 2 components, so index + 1 separators.
            chainUnits += names[index].Length;
            var exactLength = BlockLayout.MaximumPathUnits - chainUnits - (index + 1);
            for (var excess = -1; excess <= 2; excess++)
            {
                if (exactLength + excess is >= 1 and <= 400)
                {
                    AddLeaf(random, records, next++, ChainStart + (uint)index,
                        new string('e', (int)(exactLength + excess)), sequenceMayMiss: false);
                }
            }
        }

        return new FreedTrustScenario(next, 1 << 22, records, random);
    }

    // Directories ChainStart onward, each under the previous one and the first under the root; freedLinks of them
    // are freed, referenced by their child with their own sequence or one lower.
    static void AddChain(Random random, List<MftRecord> records, int chainLength, Func<int, string> nameOf,
        int freedLinks)
    {
        var freed = Enumerable.Range(0, freedLinks).Select(_ => random.Next(chainLength)).ToHashSet();
        ushort parentSequence = RootSequence;
        for (var index = 0; index < chainLength; index++)
        {
            var number = ChainStart + (uint)index;
            var isFreed = freed.Contains(index);
            var sequence = (ushort)random.Next(1, 100);
            records.Add(Record(number, index == 0 ? RootRecord : number - 1, nameOf(index), sequence, parentSequence,
                inUse: !isFreed, isDirectory: true));
            parentSequence = isFreed && random.Next(2) == 0 ? (ushort)(sequence - 1) : sequence;
        }
    }

    // A freed file under the directory, referencing its sequence, or when sequenceMayMiss a wrong one one time in eight.
    static void AddLeaf(Random random, List<MftRecord> records, uint number, uint directory, string name,
        bool sequenceMayMiss)
    {
        var directorySequence = records.First(record => record.RecordNumber == directory).SequenceNumber;
        var parentSequence = sequenceMayMiss && random.Next(8) == 0
            ? (ushort)(directorySequence + 7)
            : directorySequence;
        records.Add(Record(number, directory, name, 1, parentSequence, inUse: false, isDirectory: false));
    }

    static MftRecord? RandomRoot(Random random) => random.Next(8) switch
    {
        0 => Record(RootRecord, RootRecord, ".", RootSequence, RootSequence, inUse: false, isDirectory: true),
        1 => Record(RootRecord, RootRecord, ".", RootSequence, RootSequence, inUse: true, isDirectory: false),
        2 => null,
        3 => Record(RootRecord, 0x1_0000_0005, ".", RootSequence, RootSequence, inUse: true, isDirectory: true),
        4 => Record(RootRecord, RootRecord, "", RootSequence, RootSequence, inUse: true, isDirectory: true),
        _ => Root()
    };

    static ulong RandomParent(Random random, uint number, uint[] numbers, uint slotCapacity) => random.Next(20) switch
    {
        < 6 => RootRecord,
        < 15 => numbers[random.Next(numbers.Length)],
        15 => number,
        < 19 => (uint)random.Next((int)slotCapacity),
        _ => slotCapacity + (uint)random.Next(8)
    };

    static ushort RandomParentSequence(Random random, ushort storedSequence) => random.Next(20) switch
    {
        < 9 => storedSequence,
        < 14 => (ushort)(storedSequence - 1),
        _ => (ushort)random.Next(4)
    };

    static MftRecord Root() =>
        Record(RootRecord, RootRecord, ".", RootSequence, RootSequence, inUse: true, isDirectory: true);

    static MftRecord Record(ulong number, ulong parent, string name, ushort sequence, ushort parentSequence,
        bool inUse, bool isDirectory) =>
        new(number, parent, new MftRecordFields((ushort)((inUse ? 1 : 0) | (isDirectory ? 2 : 0)),
            isDirectory ? FileAttributes.Directory : FileAttributes.Normal, 0, 0, sequence, parentSequence), name);
}
