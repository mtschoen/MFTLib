using System.Collections.Concurrent;
using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>One scripted PowerShell launch: the script it would have run and whether it would have run elevated through the UAC verb.</summary>
public sealed class PowerShellLaunch
{
    internal PowerShellLaunch(string script, bool elevated)
    {
        Script = script;
        IsElevated = elevated;
    }

    /// <summary>The <c>Add-MpPreference</c> script text.</summary>
    public string Script { get; }

    /// <summary>true when the launch would have raised a UAC prompt; false when the process was already elevated.</summary>
    public bool IsElevated { get; }
}

/// <summary>The scope returned by <see cref="DefenderExclusionIsolation.ScriptPowerShell" />: it records each PowerShell launch instead of making it.</summary>
public sealed class ScriptedPowerShell : IDisposable
{
    readonly ConcurrentQueue<PowerShellLaunch> _launches = new();
    readonly bool _succeeds;
    readonly IDisposable _scope;

    ScriptedPowerShell(bool succeeds)
    {
        _succeeds = succeeds;
        _scope = ProcessGlobalScope.Acquire("A scripted PowerShell", () =>
        {
            DefenderExclusionSequence.RunnerOverride = Record;
            return () => DefenderExclusionSequence.RunnerOverride = null;
        });
    }

    /// <summary>The launches made so far, oldest first.</summary>
    public IReadOnlyList<PowerShellLaunch> Launches => [.. _launches];

    internal static ScriptedPowerShell Create(bool succeeds)
    {
        return new ScriptedPowerShell(succeeds);
    }

    /// <summary>Restores the real PowerShell launcher.</summary>
    public void Dispose() => _scope.Dispose();

    bool Record(string script, bool elevated)
    {
        _launches.Enqueue(new PowerShellLaunch(script, elevated));
        return _succeeds;
    }
}
