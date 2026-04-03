# MFTLib

Fast NTFS MFT (Master File Table) enumeration library for Windows. Parses MFT records via raw volume access with a native C++ core, multi-threaded parsing, and double-buffered I/O.

## Features

- Direct MFT parsing via raw volume access (no Windows Search or `FindFirstFile`)
- Native C++ I/O with parallel fixup and parsing across all CPU cores
- Double-buffered reads to overlap I/O with compute
- Native-side filename filtering (exact match or contains, case-insensitive)
- Native-side full path resolution
- Configurable buffer sizes for tuning memory/performance tradeoffs
- Benchmarked at 2.6M+ records/sec on 8M record synthetic MFT (Ryzen 9 7950X3D, Samsung 990 PRO)

## Requirements

- Windows (NTFS volumes)
- .NET 8.0+
- Administrator privileges (raw volume access)

## Usage

```csharp
using MFTLib;

// Open a volume and search for .git directories
using var volume = MftVolume.Open("C");
var records = volume.FindByName(".git", exactMatch: true, resolvePaths: true, out var timings);

foreach (var record in records.Where(r => r.IsDirectory))
    Console.WriteLine(record.FullPath);

Console.WriteLine($"Found {records.Length} matches in {timings.NativeTotalMs:F0}ms");
```

### Read all records (unfiltered)

```csharp
using var volume = MftVolume.Open("C");
var records = volume.ReadAllRecords(out var timings);
```

### Configure buffer size

```csharp
using var volume = MftVolume.Open("C");
volume.BufferSizeRecords = 65536; // 64MB buffers (default is 262144 = 256MB)
var records = volume.ReadAllRecords();
```

## Building from source

Requires Visual Studio 2022 with C++ workload. Always build with MSBuild (not `dotnet build`) since the native C++ DLL cannot be built by the .NET CLI:

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64
```

## License

[MIT](LICENSE.txt)
