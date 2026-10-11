using System.Runtime.Versioning;

// Benchmark measures raw NTFS volumes and queries WMI, so the whole assembly is Windows-only and is not part of the Linux build.
[assembly: SupportedOSPlatform("windows")]
