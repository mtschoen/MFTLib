using System.Diagnostics.CodeAnalysis;
using MFTLib;

namespace TestProgram;

/// <summary>
///     The options that tune one mode's call into the library. Each option is accepted by the modes
///     listed in <see cref="Specifications" /> and rejected on every other mode.
/// </summary>
internal sealed record ModeOptions
{
    static readonly ProgramMode[] NameFilterModes = [ProgramMode.FindName, ProgramMode.StreamRecords, ProgramMode.ParseFile];

    static readonly ProgramMode[] PathModes =
        [ProgramMode.ReadRecords, ProgramMode.FindName, ProgramMode.StreamRecords, ProgramMode.ParseFile];

    static readonly ProgramMode[] BufferModes =
    [
        ProgramMode.FindGit, ProgramMode.FindName, ProgramMode.ReadRecords, ProgramMode.StreamRecords,
        ProgramMode.UsnRead, ProgramMode.ParseFile
    ];

    const string PositiveNumber = "a positive whole number";

    static readonly OptionSpecification[] Specifications =
    [
        new("--name", "a name to match", NameFilterModes, (options, value) => options with { Name = value }),
        new("--contains", null, NameFilterModes, (options, _) => options with { Contains = true }),
        new("--include-freed", null, NameFilterModes, (options, _) => options with { IncludeFreed = true }),
        new("--no-paths", null, PathModes, (options, _) => options with { NoPaths = true }),
        new("--timings", null, [ProgramMode.ReadRecords], (options, _) => options with { Timings = true }),
        new("--buffer-size", PositiveNumber, BufferModes,
            (options, value) => uint.TryParse(value, out var parsed) && parsed > 0
                ? options with { BufferSizeRecords = parsed }
                : null),
        new("--threads", PositiveNumber, [ProgramMode.StreamRecords],
            (options, value) => int.TryParse(value, out var parsed) && parsed > 0
                ? options with { ParseThreads = parsed }
                : null),
        new("--timeout-seconds", PositiveNumber, [ProgramMode.StreamRecords],
            (options, value) => int.TryParse(value, out var parsed) && parsed > 0
                ? options with { TimeoutSeconds = parsed }
                : null),
        new("--batch-size", PositiveNumber, [ProgramMode.StreamRecords, ProgramMode.ParseFile],
            (options, value) => int.TryParse(value, out var parsed) && parsed > 0
                ? options with { BatchSize = parsed }
                : null),
        new("--stream", null, [ProgramMode.ParseFile], (options, _) => options with { Stream = true }),
        new("--maximum-size", PositiveNumber, [ProgramMode.UsnGrow],
            (options, value) => long.TryParse(value, out var parsed) && parsed > 0
                ? options with { MaximumSize = parsed }
                : null),
        new("--allocation-delta", PositiveNumber, [ProgramMode.UsnGrow],
            (options, value) => long.TryParse(value, out var parsed) && parsed > 0
                ? options with { AllocationDelta = parsed }
                : null)
    ];

    /// <summary>The name to match, or null to keep every record.</summary>
    internal string? Name { get; init; }

    /// <summary>Matches names that contain <see cref="Name" /> instead of equalling it.</summary>
    internal bool Contains { get; init; }

    /// <summary>Includes freed base records.</summary>
    internal bool IncludeFreed { get; init; }

    /// <summary>Leaves record paths unresolved.</summary>
    internal bool NoPaths { get; init; }

    /// <summary>Reports the parse phase timings of a read.</summary>
    internal bool Timings { get; init; }

    /// <summary>Records per native read chunk, or null for the library default.</summary>
    internal uint? BufferSizeRecords { get; init; }

    /// <summary>The parse thread count, or null for every processor.</summary>
    internal int? ParseThreads { get; init; }

    /// <summary>Cancels the scan after this many seconds, or null to let it run.</summary>
    internal int? TimeoutSeconds { get; init; }

    /// <summary>Records per materialized batch, or null for the library default.</summary>
    internal int? BatchSize { get; init; }

    /// <summary>Keeps the offline parse as a native result instead of copying it into an array.</summary>
    internal bool Stream { get; init; }

    /// <summary>Taken from the one positional argument of parse-file, which has no drive.</summary>
    internal string? FilePath { get; init; }

    /// <summary>What usn-grow asks the journal to grow to; the volume refuses anything at or below its current size.</summary>
    internal long? MaximumSize { get; init; }

    /// <summary>The allocation granularity usn-grow asks for, in bytes, together with <see cref="MaximumSize" />.</summary>
    internal long? AllocationDelta { get; init; }

    // Validate guarantees these are present before a mode runs, so a miss is a bug in the parser.
    internal string RequiredName => Name ?? throw MissingOption("--name");

    internal string RequiredFilePath => FilePath ?? throw MissingOption("an MFT file path");

    internal long RequiredMaximumSize => MaximumSize ?? throw MissingOption("--maximum-size");

    internal long RequiredAllocationDelta => AllocationDelta ?? throw MissingOption("--allocation-delta");

    static InvalidOperationException MissingOption(string what)
    {
        return new InvalidOperationException($"{what} was not supplied, and argument validation should have required it.");
    }

    /// <summary>A name matches exactly unless --contains; paths resolve unless --no-paths; no name keeps every record.</summary>
    internal MatchFlags ToMatchFlags()
    {
        var flags = MatchFlags.None;
        if (Name is not null)
        {
            flags |= Contains ? MatchFlags.Contains : MatchFlags.ExactMatch;
        }

        if (!NoPaths)
        {
            flags |= MatchFlags.ResolvePaths;
        }

        if (IncludeFreed)
        {
            flags |= MatchFlags.IncludeFreed;
        }

        return flags;
    }

    /// <summary>
    ///     Reads the option at <paramref name="index" /> and, for an option with a value, the argument
    ///     after it, leaving <paramref name="index" /> on the last argument consumed.
    /// </summary>
    internal static bool TryApply(ProgramMode mode, string[] arguments, ref int index, ModeOptions current,
        out ModeOptions updated, [NotNullWhen(false)] out string? error)
    {
        updated = current;
        var name = arguments[index];
        var specification = Array.Find(Specifications, candidate => candidate.Name == name);
        if (specification is null)
        {
            error = $"Unknown option {name}.";
            return false;
        }

        if (!specification.Modes.Contains(mode))
        {
            error = $"{name} does not apply to {ProgramModes.NameOf(mode)}.";
            return false;
        }

        var value = string.Empty;
        if (specification.ValueDescription is not null)
        {
            if (index + 1 >= arguments.Length)
            {
                error = $"{name} needs {specification.ValueDescription}.";
                return false;
            }

            value = arguments[++index];
        }

        var applied = specification.Apply(current, value);
        if (applied is null)
        {
            error = $"{name} needs {specification.ValueDescription}.";
            return false;
        }

        updated = applied;
        error = null;
        return true;
    }

    /// <summary>Checks the combinations a single option cannot check: what a mode requires and what needs what.</summary>
    /// <returns>The problem with the options, or null when they are consistent.</returns>
    internal string? Validate(ProgramMode mode, int positionalCount)
    {
        return mode switch
        {
            ProgramMode.FindName when Name is null => "find-name needs --name.",
            ProgramMode.ParseFile when positionalCount != 1 => "parse-file needs exactly one MFT file path.",
            ProgramMode.ParseFile when BatchSize is not null && !Stream => "--batch-size needs --stream on parse-file.",
            ProgramMode.UsnGrow when positionalCount != 1 =>
                "usn-grow needs exactly one drive letter; it never defaults to a drive.",
            ProgramMode.UsnGrow when MaximumSize is null || AllocationDelta is null =>
                "usn-grow needs both --maximum-size and --allocation-delta.",
            _ when Contains && Name is null => "--contains needs --name.",
            _ => null
        };
    }

    sealed record OptionSpecification(
        string Name,
        string? ValueDescription,
        ProgramMode[] Modes,
        Func<ModeOptions, string, ModeOptions?> Apply);
}
