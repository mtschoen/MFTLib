namespace MFTLibTestExtensions;

/// <summary>
///     Every pipe end the in-process host was handed, so the host's exit can close them all the way
///     a broker process's exit closes its handles. An end handed out after the exit is closed at once.
/// </summary>
internal sealed class HostPipeEnds
{
    readonly Lock _gate = new();
    readonly List<Stream> _ends = [];
    bool _exited;

    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _exited;
            }
        }
    }

    public void Track(Stream end)
    {
        lock (_gate)
        {
            if (!_exited)
            {
                _ends.Add(end);
                return;
            }
        }

        end.Dispose();
    }

    /// <summary>The host's exit: closes every end it holds, so the client reads EOF on each.</summary>
    public void CloseAll()
    {
        Stream[] ends;
        lock (_gate)
        {
            _exited = true;
            ends = _ends.ToArray();
            _ends.Clear();
        }

        foreach (var end in ends)
        {
            end.Dispose();
        }
    }
}
