using System.Diagnostics;
using System.Runtime.ExceptionServices;
using MFTLib.Index;

namespace Benchmark;

internal readonly record struct MemoryReading(
    long PrivateBytes, long PeakPrivateBytes, long PeakWorkingSetBytes);

internal sealed record ScenarioMemoryResult(
    long PeakPrivateBytes, long PeakWorkingSetBytes, string Method, int ProcessId);

internal sealed record ScenarioMemoryRequest(
    string Scenario, string InputPath, int Iterations, IndexMemoryInput? Index = null, uint Rows = 0);

internal sealed record IndexMemoryInput(
    char Drive, string RootDirectory, uint VolumeSerial, ProducerKind Producer,
    uint PackedFourCc, uint TagVersion, ulong ScanShape);

internal sealed class ScenarioMemoryCapture : IAsyncDisposable
{
    readonly object _lock = new();
    readonly Func<MemoryReading> _read;
    readonly bool _useOperatingSystemPeak;
    readonly string _method;
    readonly Process? _ownedProcess;
    ITimer? _timer;
    long _peakPrivateBytes;
    long _peakWorkingSetBytes;
    ExceptionDispatchInfo? _failure;
    bool _disposed;

    public ScenarioMemoryCapture()
        : this(CreateProcessReader(out var ownedProcess), OperatingSystem.IsWindows(), TimeProvider.System)
    {
        _ownedProcess = ownedProcess;
    }

    internal ScenarioMemoryCapture(Func<MemoryReading> read, bool useOperatingSystemPeak, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _read = read;
        _useOperatingSystemPeak = useOperatingSystemPeak;
        _method = useOperatingSystemPeak ? "windows-os-peak" : "linux-sampled-vmdata-10ms";
        Sample();
        if (!useOperatingSystemPeak)
        {
            _timer = timeProvider.CreateTimer(
                _ => SampleSafely(), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        }
    }

    internal static Func<MemoryReading> CreateProcessReader(out Process ownedProcess)
    {
        EnsureSupportedPlatform(OperatingSystem.IsWindows(), OperatingSystem.IsLinux());
        var process = Process.GetCurrentProcess();
        ownedProcess = process;
        return () => Read(process);
    }

    internal static void EnsureSupportedPlatform(bool isWindows, bool isLinux)
    {
        if (!isWindows && !isLinux)
        {
            throw new PlatformNotSupportedException("Benchmark private-memory measurement requires Windows or Linux.");
        }
    }

    static MemoryReading Read(Process process)
    {
        process.Refresh();
        return new MemoryReading(
            process.PrivateMemorySize64,
            OperatingSystem.IsWindows() ? process.PeakPagedMemorySize64 : 0,
            process.PeakWorkingSet64);
    }

    internal void Sample()
    {
        lock (_lock)
        {
            _failure?.Throw();
            SampleUnderLock();
        }
    }

    void SampleSafely()
    {
        lock (_lock)
        {
            if (_failure is not null || _disposed)
            {
                return;
            }

            try
            {
                SampleUnderLock();
            }
            catch (Exception exception)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    void SampleUnderLock()
    {
        var reading = _read();
        var privateBytes = _useOperatingSystemPeak ? reading.PeakPrivateBytes : reading.PrivateBytes;
        _peakPrivateBytes = Math.Max(_peakPrivateBytes, privateBytes);
        _peakWorkingSetBytes = Math.Max(_peakWorkingSetBytes, reading.PeakWorkingSetBytes);
    }

    internal async Task<ScenarioMemoryResult> FinishAsync()
    {
        SampleSafely();
        await DrainTimerAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _failure?.Throw();
            if (_peakPrivateBytes <= 0 || _peakWorkingSetBytes <= 0)
            {
                throw new InvalidOperationException("Process memory counters are unavailable.");
            }

            return new ScenarioMemoryResult(_peakPrivateBytes, _peakWorkingSetBytes, _method, Environment.ProcessId);
        }
    }

    async ValueTask DrainTimerAsync()
    {
        ITimer? timer;
        lock (_lock)
        {
            timer = _timer;
            _timer = null;
        }

        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DrainTimerAsync().ConfigureAwait(false);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ownedProcess?.Dispose();
        }
    }
}
