using System.Numerics;
using MFTLib.Index;

namespace Benchmark;

/// <summary>
///     Spike: consumer-side port of the library duplicate-name sieve, public API only. The bitmap
///     helpers are shared; the two drivers (CopyName and ForEachRow) follow.
/// </summary>
static class ConsumerSieve
{
    const int MaximumRefinementPassCount = 3;
    const int MinimumBucketCount = 1 << 16;
    const int MaximumBucketCount = 1 << 27;

    // ---- sieve bitmaps (shared by A and B) ----
    internal sealed class Pass(int bucketCount, int seed)
    {
        readonly int _shift = 32 - BitOperations.Log2((uint)bucketCount);
        readonly ulong[] _seenOnce = new ulong[bucketCount / 64];
        readonly ulong[] _seenAgain = new ulong[bucketCount / 64];

        int Bucket(int hash)
        {
            var value = (uint)hash + unchecked((uint)seed * 0x9E3779B9);
            value ^= value >> 16; value *= 0x85EBCA6Bu; value ^= value >> 13; value *= 0xC2B2AE35u; value ^= value >> 16;
            return (int)(value >> _shift);
        }

        public void Increment(int hash)
        {
            var index = Bucket(hash);
            if ((_seenOnce[index >> 6] & (1UL << (index & 63))) != 0) _seenAgain[index >> 6] |= 1UL << (index & 63);
            else _seenOnce[index >> 6] |= 1UL << (index & 63);
        }

        public bool MayRepeat(int hash)
        {
            var index = Bucket(hash);
            return (_seenAgain[index >> 6] & (1UL << (index & 63))) != 0;
        }
    }

    internal static int ComputeBucketCount(long rowCount)
    {
        var target = Math.Min(rowCount, MaximumBucketCount) * 4;
        return target > MaximumBucketCount ? MaximumBucketCount
            : Math.Max((int)BitOperations.RoundUpToPowerOf2((ulong)target), MinimumBucketCount);
    }

    static bool PassesEarlier(List<Pass> passes, int hash)
    {
        foreach (var pass in passes) if (!pass.MayRepeat(hash)) return false;
        return true;
    }

    // Driver contract: hashScan feeds one hash per eligible row; materializeScan sees name + entry for survivors.
    static Dictionary<string, List<FileEntry>> Run(long rowCount, Action<Action<int>> hashScan,
        Action<Func<int, bool>, Dictionary<string, List<FileEntry>>> materializeScan)
    {
        var bucketCount = ComputeBucketCount(rowCount);
        var passes = new List<Pass>();
        var previous = long.MaxValue;
        for (var passIndex = 0; passIndex <= MaximumRefinementPassCount; passIndex++)
        {
            var pass = new Pass(bucketCount, passIndex);
            long candidates = 0;
            hashScan(hash =>
            {
                if (!PassesEarlier(passes, hash)) return;
                pass.Increment(hash);
                candidates++;
            });
            passes.Add(pass);
            var stop = candidates <= bucketCount / 256 || candidates >= previous / 2 || passIndex == MaximumRefinementPassCount;
            previous = candidates;
            if (stop) break;
        }

        var byName = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        materializeScan(hash => PassesEarlier(passes, hash), byName);
        return byName;
    }

    // ---- variant A: Enumerate + CopyName ----
    public static Dictionary<string, List<FileEntry>> FindWithCopyName(FileIndex index, long rowCount, CancellationToken token)
    {
        var query = new SearchQuery(null, Directories: false);
        var buffer = new char[260];
        int Hash(FileEntry entry, out int length)
        {
            length = entry.CopyName(buffer);
            if (length > buffer.Length) { buffer = new char[length]; length = entry.CopyName(buffer); }
            return length == 0 ? 0 : string.GetHashCode(buffer.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
        }

        return Run(rowCount,
            sink => { foreach (var entry in index.Enumerate(query, token)) { var hash = Hash(entry, out var length); if (length != 0) sink(hash); } },
            (passes, byName) =>
            {
                foreach (var entry in index.Enumerate(query, token))
                {
                    var hash = Hash(entry, out var length);
                    if (length == 0 || !passes(hash)) continue;
                    var key = new string(buffer, 0, length);
                    if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
                    list.Add(entry);
                }
            });
    }

    // ---- variant B: ForEachRow ----
    sealed class HashVisitor(Action<int> sink) : IIndexRowVisitor
    {
        public void Visit(in IndexRow row)
        {
            if (!row.Name.IsEmpty) sink(string.GetHashCode(row.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    sealed class MaterializeVisitor(Func<int, bool> passes, Dictionary<string, List<FileEntry>> byName) : IIndexRowVisitor
    {
        public void Visit(in IndexRow row)
        {
            if (row.Name.IsEmpty || !passes(string.GetHashCode(row.Name, StringComparison.OrdinalIgnoreCase))) return;
            var key = new string(row.Name);
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
            list.Add(row.ToEntry());
        }
    }

    // Struct forwarders select the generic (no interface dispatch on the library side) overload.
    struct HashForwarder(HashVisitor inner) : IIndexRowVisitor { public void Visit(in IndexRow row) => inner.Visit(in row); }
    struct MaterializeForwarder(MaterializeVisitor inner) : IIndexRowVisitor { public void Visit(in IndexRow row) => inner.Visit(in row); }

    public static Dictionary<string, List<FileEntry>> FindWithVisitor(FileIndex index, long rowCount, bool generic, CancellationToken token)
    {
        var query = new SearchQuery(null, Directories: false);
        return Run(rowCount,
            sink =>
            {
                var visitor = new HashVisitor(sink);
                if (generic) { var forwarder = new HashForwarder(visitor); index.ForEachRow(query, ref forwarder, token); }
                else index.ForEachRow(query, visitor, token);
            },
            (passes, byName) =>
            {
                var visitor = new MaterializeVisitor(passes, byName);
                if (generic) { var forwarder = new MaterializeForwarder(visitor); index.ForEachRow(query, ref forwarder, token); }
                else index.ForEachRow(query, visitor, token);
            });
    }
}
