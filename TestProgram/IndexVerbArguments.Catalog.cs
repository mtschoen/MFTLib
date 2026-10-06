namespace TestProgram;

// The Index verbs and the options they accept.
internal sealed partial record IndexVerbArguments
{
    internal static readonly string[] Verbs =
        ["search", "tree", "open", "largest", "duplicate-names", "rescan", "watch", "journal", "cache", "elevation-status"];

    static readonly HashSet<string> Flags =
    [
        "--stream", "--exact", "--case-sensitive", "--directories", "--files", "--cache-only", "--no-cache",
        "--inspect-session", "--clear", "--ensure-created"
    ];

    static readonly HashSet<string> ValueOptions =
    [
        "--source", "--root", "--profile", "--keep-name", "--cache-tag", "--cache-directory", "--diagnostics",
        "--connection-timeout", "--name", "--under", "--min-size", "--max-size", "--after", "--before", "--limit",
        "--path", "--record-key", "--depth", "--count", "--drive-scope", "--drive-list", "--seconds",
        "--maximum-size", "--allocation-delta"
    ];

    static readonly HashSet<string> CommonIndexOptions =
    [
        "--source", "--root", "--cache-only", "--no-cache", "--profile", "--keep-name", "--cache-tag",
        "--cache-directory", "--diagnostics", "--connection-timeout"
    ];

    static readonly Dictionary<string, HashSet<string>> AllowedOptionsByVerb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] =
        [
            .. CommonIndexOptions, "--name", "--exact", "--case-sensitive", "--under", "--directories", "--files",
            "--min-size", "--max-size", "--after", "--before", "--stream", "--limit"
        ],
        ["tree"] = [.. CommonIndexOptions, "--path", "--record-key", "--depth"],
        ["open"] = [.. CommonIndexOptions, "--path", "--count"],
        ["largest"] = [.. CommonIndexOptions, "--count", "--under"],
        ["duplicate-names"] = [.. CommonIndexOptions, "--limit"],
        ["rescan"] = [.. CommonIndexOptions, "--drive-scope", "--drive-list"],
        ["watch"] = [.. CommonIndexOptions, "--drive-scope", "--drive-list", "--seconds", "--inspect-session"],
        ["journal"] = [.. CommonIndexOptions, "--maximum-size", "--allocation-delta"],
        ["cache"] = ["--cache-directory", "--cache-tag", "--ensure-created", "--clear"],
        ["elevation-status"] = []
    };

    internal static string Usage =>
        "Index verbs: " + string.Join(", ", Verbs) + " [drive ...] [options]" + Environment.NewLine +
        "  index options: --source broker|enumeration|unavailable --root DIR --cache-only --no-cache" + Environment.NewLine +
        "    --profile full|directories --keep-name NAME --cache-tag FOURCC:N --cache-directory DIR --diagnostics DIR" +
        Environment.NewLine +
        "    --connection-timeout S; run unattended with --source enumeration --root DIR --no-cache" + Environment.NewLine +
        "  search --name --exact --case-sensitive --under --directories|--files --min-size --max-size --after --before" +
        " --stream --limit" + Environment.NewLine +
        "  tree [--path P | --record-key DRIVE:ROW:PRODUCER] --depth   open --path P --count N   largest --count --under" +
        Environment.NewLine +
        "  rescan|watch [--drive-scope L | --drive-list L,M] watch: --seconds --inspect-session" + Environment.NewLine +
        "  journal DRIVE [--maximum-size B --allocation-delta B]   cache [--cache-directory D --ensure-created --clear" +
        " --cache-tag T]";

    internal static bool IsVerb(string name)
    {
        return Verbs.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
