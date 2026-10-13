using System.Collections.Concurrent;
using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>One scripted PowerShell launch: the script it would have run and whether it would have run elevated through the UAC verb.</summary>
public sealed class PowerShellLaunch
{
    internal PowerShellLaunch(string script, bool elevated)
    {
        Script = script;
        Elevated = elevated;
    }

    /// <summary>The <c>Add-MpPreference</c> script text.</summary>
    public string Script { get; }

    /// <summary>true when the launch would have raised a UAC prompt; false when the process was already elevated.</summary>
    public bool Elevated { get; }
}

/// <summary>The scope returned by <see cref="DefenderExclusionIsolation.ScriptPowerShell" />: it records each PowerShell launch instead of making it.</summary>
public sealed class ScriptedPowerShell : IDisposable
{
    static readonly Lock OwnershipLock = new();
    static ScriptedPowerShell? _active;
    readonly ConcurrentQueue<PowerShellLaunch> _launches = new();
    readonly bool _succeeds;
    bool _disposed;

    ScriptedPowerShell(bool succeeds)
    {
        _succeeds = succeeds;
    }

    /// <summary>The launches made so far, oldest first.</summary>
    public IReadOnlyList<PowerShellLaunch> Launches => [.. _launches];

    internal static ScriptedPowerShell Create(bool succeeds)
    {
        lock (OwnershipLock)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    "A scripted PowerShell is already active. " +
                    "Use nonparallel fixtures and dispose the owning scope before acquiring another.");
            }

            var scope = new ScriptedPowerShell(succeeds);
            DefenderExclusionSequence.RunnerOverride = scope.Record;
            _active = scope;
            return scope;
        }
    }

    /// <summary>Restores the real PowerShell launcher.</summary>
    public void Dispose()
    {
        lock (OwnershipLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _active = null;
            DefenderExclusionSequence.RunnerOverride = null;
        }
    }

    bool Record(string script, bool elevated)
    {
        _launches.Enqueue(new PowerShellLaunch(script, elevated));
        return _succeeds;
    }
}
