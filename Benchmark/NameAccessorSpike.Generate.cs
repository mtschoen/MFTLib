using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

/// <summary>Spike: writes a synthetic cache block with a realistic name distribution. Uses internals (BlockFile) on purpose; the measured variants do not.</summary>
static class NameAccessorSpikeGenerator
{
    static readonly string[] Words =
    [
        "index", "main", "util", "helper", "config", "settings", "readme", "license", "package", "module",
        "controller", "service", "repository", "component", "template", "migration", "fixture", "benchmark",
        "documentation", "implementation", "integration", "authentication", "configuration", "photo", "report",
        "invoice", "backup", "archive", "thumbnail", "screenshot", "notes", "draft", "final", "copy"
    ];

    static readonly string[] Extensions =
        [".js", ".ts", ".cs", ".py", ".md", ".json", ".txt", ".png", ".jpg", ".dll", ".log", ".xml", ".h", ".cpp", ".tmp"];

    static readonly string[] Famous =
    [
        "index.js", "README.md", "__init__.py", "desktop.ini", "package.json", "LICENSE", ".gitignore", "main.cs",
        "Thumbs.db", "index.html", "style.css", "setup.py", "Makefile", "CHANGELOG.md", "tsconfig.json", ".DS_Store"
    ];

    public static void Generate(string directory, uint rows)
    {
        Directory.CreateDirectory(directory);
        var random = new Random(12345);
        var commonPool = BuildCommonPool(random);
        var directories = new List<uint> { 0 };
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(directory, "T-00000000.mlix"),
            VolumeSerial = 0,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(rows),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(checked(rows * 56 + (uint)directory.Length * 2))
        });
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, directory, new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
        for (uint row = 1; row < rows; row++)
        {
            if (random.Next(40) == 0)
            {
                var parent = directories[random.Next(directories.Count)];
                writer.TryWriteRow(row, $"dir{random.Next(100000)}_{row:x}",
                    new RowColumns(parent, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
                directories.Add(row);
                continue;
            }

            var fileParent = directories[random.Next(directories.Count)];
            var name = ChooseFileName(random, commonPool, row, rows);
            var flags = RowFlags.InUse;
            var size = (long)Math.Exp(random.NextDouble() * 22);
            var roll = random.Next(100);
            if (roll == 0)
            {
                flags |= RowFlags.Tombstone;
                size *= 1000;
            }
            else if (roll < 3)
            {
                flags |= RowFlags.SizeUnknown;
                size = 0;
            }

            if (!writer.TryWriteRow(row, name, new RowColumns(fileParent, flags, 0, size, 0, 0)))
            {
                throw new InvalidOperationException($"Block full at row {row}.");
            }
        }

        writer.Complete(DateTime.UtcNow, null);
    }

    static string[] BuildCommonPool(Random random)
    {
        var pool = new List<string>(Famous);
        for (var index = 0; index < 4000; index++)
        {
            pool.Add(Words[random.Next(Words.Length)] + random.Next(500).ToString(CultureInfo.InvariantCulture) +
                     Extensions[random.Next(Extensions.Length)]);
        }

        return [.. pool];
    }

    static string ChooseFileName(Random random, string[] commonPool, uint row, uint rows)
    {
        var roll = random.Next(100);
        if (roll < 25)
        {
            // Skewed toward the head so a few names repeat hundreds of thousands of times.
            var name = commonPool[(int)(commonPool.Length * Math.Pow(random.NextDouble(), 3))];
            return random.Next(50) == 0 ? name.ToUpperInvariant() : name;
        }

        var word = Words[random.Next(Words.Length)];
        var extension = Extensions[random.Next(Extensions.Length)];
        if (roll < 35)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{word}-{random.Next((int)(rows / 8))}{extension}");
        }

        var padding = new string((char)('a' + random.Next(26)), random.Next(0, 12));
        return string.Create(CultureInfo.InvariantCulture, $"{word}{padding}_{row:x}{extension}");
    }
}
