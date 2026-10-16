using System.Runtime.Versioning;

// Benchmark queries WMI for the system information in its report, so analyzers treat the whole assembly as Windows-only.
[assembly: SupportedOSPlatform("windows")]
