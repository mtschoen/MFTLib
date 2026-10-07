#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// The option plumbing both samples share: it reads "--name value" pairs and bare flags out of a command line and
// leaves the positional arguments. The first problem it meets is the one it reports.
internal sealed partial class ArgumentReader(IEnumerable<string> arguments)
{
    readonly List<string> _remaining = [.. arguments];
    readonly List<string> _supplied = [];

    /// <summary>The options the command line carried, in the order they were read, so a parser can refuse one the chosen verb never uses.</summary>
    internal IReadOnlyList<string> Supplied => _supplied;

    internal string? Error { get; private set; }

    internal bool Flag(string name)
    {
        var present = _remaining.RemoveAll(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (present)
        {
            _supplied.Add(name);
        }

        return present;
    }

    internal string? Text(string name)
    {
        var index = _remaining.FindIndex(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        _supplied.Add(name);
        if (index + 1 >= _remaining.Count || _remaining[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Error ??= $"Option {name} needs a value.";
            _remaining.RemoveAt(index);
            return null;
        }

        var value = _remaining[index + 1];
        _remaining.RemoveRange(index, 2);
        return value;
    }
}
