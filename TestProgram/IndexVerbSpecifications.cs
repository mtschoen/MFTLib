namespace TestProgram;

/// <summary>
///     The table of Index verbs. Every verb that opens an index takes the opener options, so one
///     command line shape selects the source, the cache behavior and the broker settings for all of them.
/// </summary>
internal static class IndexVerbSpecifications
{
    internal const int DefaultLimit = 20;

    static readonly OptionSpecification[] OpenerOptions =
    [
        new("--source", OptionKind.Choice,
            [IndexVerbArguments.BrokerSource, IndexVerbArguments.EnumerationSource, IndexVerbArguments.UnavailableSource]),
        new("--root", OptionKind.Text),
        new("--cache-only", OptionKind.Flag),
        new("--no-cache", OptionKind.Flag),
        new("--profile", OptionKind.Choice, [IndexVerbArguments.FullProfile, IndexVerbArguments.DirectoriesProfile]),
        new("--keep-name", OptionKind.Text, Repeatable: true),
        new("--cache-tag", OptionKind.CacheTag),
        new("--cache-directory", OptionKind.Text),
        new("--diagnostics", OptionKind.Text),
        new("--connection-timeout", OptionKind.Seconds),
        new("--volume-serial", OptionKind.Serial),
        new("--unavailable-reason", OptionKind.Text),
        new("--timeout-seconds", OptionKind.Seconds)
    ];

    static readonly OptionSpecification[] FilterOptions =
    [
        new("--name", OptionKind.Text),
        new("--case-sensitive", OptionKind.Flag),
        new("--under", OptionKind.Text),
        new("--directories", OptionKind.Flag),
        new("--files", OptionKind.Flag),
        new("--min-size", OptionKind.Size),
        new("--max-size", OptionKind.Size),
        new("--after", OptionKind.Date),
        new("--before", OptionKind.Date),
        new("--limit", OptionKind.Count)
    ];

    static readonly OptionSpecification[] ScopeOptions =
    [
        new("--drive-scope", OptionKind.Text),
        new("--drive-list", OptionKind.Letters),
        new("--all", OptionKind.Flag)
    ];

    static readonly string[] ScopeOptionNames = ["--drive-scope", "--drive-list", "--all"];

    static readonly OptionSpecification Path = new("--path", OptionKind.Text);
    static readonly OptionSpecification Limit = new("--limit", OptionKind.Count);
    static readonly OptionSpecification CacheDirectory = new("--cache-directory", OptionKind.Text);
    static readonly OptionSpecification CacheTag = new("--cache-tag", OptionKind.CacheTag);

    internal static readonly IReadOnlyList<VerbSpecification> All =
    [
        new("search", "Searches the index; prints the effective query first.", DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, .. FilterOptions, new("--exact", OptionKind.Flag)], Search),
        new("enumerate", "Enumerates matches lazily and disposes the enumerator on early exit.",
            DriveRule.DefaultsToSampleDrive, [.. OpenerOptions, .. FilterOptions], Enumerate),
        new("find-path", "Finds an entry by native path and prints each drive's root.",
            DriveRule.DefaultsToSampleDrive, [.. OpenerOptions, Path], Opener),
        new("tree", "Walks Root, Parent and Children, or selects an entry by record key.",
            DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, Path, new("--record-key", OptionKind.RecordKey), new("--depth", OptionKind.Count), Limit],
            Tree),
        new("open", "Opens a file entry and prints its first bytes.", DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, Path, new("--bytes", OptionKind.Count)], Open),
        new("largest", "Prints the largest files, optionally under a path.", DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, new("--count", OptionKind.Count), new("--under", OptionKind.Text)], Opener),
        new("duplicate-names", "Prints names shared by more than one entry.", DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, Limit], Opener),
        new("rescan", "Rescans one drive, a list of drives or every drive.", DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, .. ScopeOptions], Scoped),
        new("watch", "Starts, waits for catch-up, observes and stops the live watch at a chosen scope.",
            DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, .. ScopeOptions, new("--seconds", OptionKind.Seconds),
                new("--inspect-session", OptionKind.Flag)], Scoped),
        new("journal-status", "Prints journal sizing and every checkpoint-loss field.", DriveRule.DefaultsToSampleDrive,
            [.. OpenerOptions, new("--wait-catch-up", OptionKind.Flag)], Opener),
        new("journal-grow", "Grows one drive's USN journal to explicit sizes through the broker.", DriveRule.ExactlyOne,
            [new("--maximum-size", OptionKind.PositiveNumber), new("--allocation-delta", OptionKind.PositiveNumber),
                new("--connection-timeout", OptionKind.Seconds), new("--diagnostics", OptionKind.Text),
                new("--timeout-seconds", OptionKind.Seconds)], JournalGrow),
        new("cache-inspect", "Lists cached blocks with their validation and identity.", DriveRule.OptionalFilter,
            [CacheDirectory, CacheTag, new("--ensure-created", OptionKind.Flag)], _ => null),
        new("cache-clear", "Deletes cached blocks under an explicit cache directory.", DriveRule.OptionalFilter,
            [CacheDirectory, new("--default-cache", OptionKind.Flag)], CacheClear),
        new("elevation-status", "Prints what this process knows about elevation.", DriveRule.None,
            [new("--relaunch-elevated", OptionKind.Flag)], _ => null)
    ];

    internal static VerbSpecification? Find(string name)
    {
        return All.FirstOrDefault(verb => string.Equals(verb.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static string Usage
    {
        get
        {
            var lines = new List<string> { "Index verbs: <verb> [drive ...] [options]" };
            foreach (var verb in All)
            {
                lines.Add($"  {verb.Name} - {verb.Summary}");
                lines.Add("      " + string.Join(" ", verb.Options.Select(option => option.Name)));
            }

            lines.Add("  Every index verb accepts --source broker|enumeration|unavailable; run unattended with");
            lines.Add("  --source enumeration --root DIRECTORY --no-cache (no elevation, no dialog).");
            return string.Join(Environment.NewLine, lines);
        }
    }

    static string? Opener(IndexVerbArguments arguments)
    {
        var source = arguments.Source;
        if (source != IndexVerbArguments.BrokerSource)
        {
            if (arguments.Has("--profile") || arguments.Has("--keep-name") || arguments.Has("--connection-timeout"))
            {
                return "--profile, --keep-name and --connection-timeout apply only to --source broker.";
            }
        }

        if (source == IndexVerbArguments.BrokerSource)
        {
            if (arguments.Has("--root") || arguments.Has("--volume-serial"))
            {
                return "--root and --volume-serial apply only to --source enumeration or unavailable.";
            }
        }

        if (source == IndexVerbArguments.EnumerationSource && !arguments.Has("--root"))
        {
            return "--source enumeration needs --root DIRECTORY.";
        }

        if (source != IndexVerbArguments.UnavailableSource && arguments.Has("--unavailable-reason"))
        {
            return "--unavailable-reason applies only to --source unavailable.";
        }

        if (arguments.Has("--volume-serial") && !arguments.Has("--root"))
        {
            return "--volume-serial needs --root.";
        }

        if (arguments.Has("--root") && arguments.Drives.Count > 1)
        {
            return "--root names one directory, so it takes one drive letter.";
        }

        return arguments.Has("--keep-name") && arguments.Text("--profile") != IndexVerbArguments.DirectoriesProfile
            ? "--keep-name needs --profile directories."
            : null;
    }

    static string? Filters(IndexVerbArguments arguments)
    {
        if (arguments.Has("--directories") && arguments.Has("--files"))
        {
            return "--directories and --files exclude each other.";
        }

        if (arguments.Number("--min-size") is { } minimum && arguments.Number("--max-size") is { } maximum &&
            minimum > maximum)
        {
            return "--min-size is larger than --max-size.";
        }

        return arguments.Date("--after") is { } after && arguments.Date("--before") is { } before && after > before
            ? "--after is later than --before."
            : null;
    }

    static string? Search(IndexVerbArguments arguments)
    {
        if (Opener(arguments) is { } openerProblem)
        {
            return openerProblem;
        }

        if (arguments.Has("--exact") &&
            (!arguments.Has("--name") || FilterOptions.Any(option => option.Name is not ("--name" or "--limit") &&
                arguments.Has(option.Name))))
        {
            return "--exact needs --name and filters by name only (FindByName); drop the other filters.";
        }

        return Filters(arguments);
    }

    static string? Enumerate(IndexVerbArguments arguments)
    {
        return Opener(arguments) ?? Filters(arguments);
    }

    static string? Tree(IndexVerbArguments arguments)
    {
        return arguments.Has("--path") && arguments.Has("--record-key")
            ? "--path and --record-key select the start differently; give one."
            : Opener(arguments);
    }

    static string? Open(IndexVerbArguments arguments)
    {
        return arguments.Has("--path") ? Opener(arguments) : "open needs --path FILE.";
    }

    static string? Scoped(IndexVerbArguments arguments)
    {
        if (Opener(arguments) is { } openerProblem)
        {
            return openerProblem;
        }

        var scopes = ScopeOptionNames.Count(arguments.Has);
        if (scopes > 1)
        {
            return "--drive-scope, --drive-list and --all select one scope each; give one.";
        }

        if (arguments.Text("--drive-scope") is { } single &&
            (!IndexVerbArguments.TryParseDrive(single, out var letter) || !arguments.Drives.Contains(letter)))
        {
            return "--drive-scope needs one of the opened drive letters.";
        }

        return arguments.Letters("--drive-list") is { } list && list.Any(letter => !arguments.Drives.Contains(letter))
            ? "--drive-list names a drive that is not opened."
            : null;
    }

    static string? JournalGrow(IndexVerbArguments arguments)
    {
        if (arguments.Drives.Count != 1)
        {
            return "journal-grow needs exactly one drive letter; it never defaults to a drive.";
        }

        return arguments.Has("--maximum-size") && arguments.Has("--allocation-delta")
            ? null
            : "journal-grow needs both --maximum-size and --allocation-delta.";
    }

    static string? CacheClear(IndexVerbArguments arguments)
    {
        return arguments.Has("--cache-directory") == arguments.Has("--default-cache")
            ? "cache-clear needs exactly one of --cache-directory DIRECTORY and --default-cache."
            : null;
    }
}
