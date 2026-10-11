using System.Runtime.Versioning;

// Benchmark measures raw NTFS volumes and queries WMI, so analyzers treat the whole assembly as Windows-only.
[assembly: SupportedOSPlatform("windows")]
