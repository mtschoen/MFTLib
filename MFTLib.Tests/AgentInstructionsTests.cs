using System.Text;
using System.Text.RegularExpressions;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public partial class AgentInstructionsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void WholeDocument_RemainsBelowCharacterBudget()
    {
        var text = ReadInstructions();
        var characters = CountCharacters(text);
        if (characters > 14500)
        {
            TestContext.WriteLine(
                $"warning: AGENTS.md has {characters} characters, within 500 of the 15000 budget.");
        }

        Assert.IsLessThan(15000, characters,
            $"AGENTS.md has {characters} characters; it must stay below 15000. " +
            "Move narrative unchanged into linked docs pages.");
    }

    [TestMethod]
    public void EveryBlock_RemainsBelowCharacterBudget()
    {
        var blocks = MyRegex().Split(ReadInstructions());
        var longest = blocks.Max(CountCharacters);
        Assert.IsLessThan(3000, longest,
            $"AGENTS.md has a {longest}-character block; each must stay below 3000. " +
            "Separate concise rules with blank lines.");
    }

    [TestMethod]
    public void EveryLinkedDocsPage_Exists()
    {
        var links = LinkedDocsRegex().Matches(ReadInstructions())
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.IsNotEmpty(links, "AGENTS.md links no docs pages.");
        foreach (var link in links)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "repository-docs", link);
            Assert.IsTrue(File.Exists(path), $"AGENTS.md links {link}, which does not exist.");
        }
    }

    [TestMethod]
    public void ScannerAndConsumerWorkflows_UseServerContext()
    {
        var scanner = RepositoryFiles.Read(".gitea/workflows/aislop.yml");
        Assert.IsTrue(scanner.Contains(
            "AISLOP_REPOSITORY_URL: ${{ format('{0}/schoen/aislop.git', github.server_url) }}",
            StringComparison.Ordinal));
        Assert.IsTrue(scanner.Contains("git clone \"$env:AISLOP_REPOSITORY_URL\" $aislopDir",
            StringComparison.Ordinal));
        Assert.IsTrue(scanner.Contains("git -C $aislopDir checkout \"$env:AISLOP_FORK_COMMIT\"",
            StringComparison.Ordinal));
        Assert.IsFalse(scanner.Contains("NODE_EXTRA_CA_CERTS:", StringComparison.Ordinal));
        var consumer = RepositoryFiles.Read(".gitea/workflows/sync-consumers.yml");
        Assert.IsTrue(consumer.Contains("GITEA_URL: ${{ github.server_url }}", StringComparison.Ordinal));
        Assert.IsTrue(consumer.Contains("GITEA_TOKEN: ${{ secrets.MFTLIB_SYNC_TOKEN }}", StringComparison.Ordinal));
    }

    // Assemble detector signatures so the guard can scan its own source without exemptions.
    static string[] PrivateContentPatterns() =>
    [
        @"(?i)[a-z]:[\\/]+Users(?:[\\/]|\b)",
        @"(?i)/mnt/[a-z]/Users(?:/|\b)",
        "(?i)" + Regex.Escape(string.Join('.', "fleet", "sticktoitive", "net")),
        string.Concat(@"(?i)\b", "chon", @"kers\b"),
        string.Concat(@"(?i)\b", "llama", @"box\b"),
        Regex.Escape(string.Concat("~/", "schoen-lab/"))
    ];

    [TestMethod]
    public void TrackedFiles_ContainNoPrivateDeploymentReferences()
    {
        var paths = RepositoryFiles.Git("ls-files", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsNotEmpty(paths, "Git returned no tracked files.");
        Assert.Contains(".plan", paths, "The backlog must remain tracked.");
        var patterns = PrivateContentPatterns();
        var failures = new List<string>();
        foreach (var path in paths)
        {
            var text = RepositoryFiles.Read(path);
            for (var index = 0; index < patterns.Length; index++)
            {
                if (Regex.IsMatch(text, patterns[index]))
                {
                    failures.Add($"{path}: private-content rule {index + 1}");
                }
            }
        }

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        Assert.DoesNotContain(".maintenance.json", paths, "Maintenance state must be local.");
        Assert.AreEqual(".maintenance.json",
            RepositoryFiles.Git("check-ignore", "--no-index", ".maintenance.json").Trim());
        Assert.DoesNotContain("TEST-REPORT", RepositoryFiles.Read(".gitignore"),
            "CI tracks the generated report on main, so it must not be ignored.");
        if (paths.Contains("TEST-REPORT.md"))
        {
            // The loop above already applied every private-content rule to the tracked report.
            var report = RepositoryFiles.Read("TEST-REPORT.md");
            Assert.StartsWith("# Test report: generated on main by CI; must not be edited in a pull request",
                report, "A tracked report must carry the generator's provenance header.");
            Assert.IsTrue(ReportSourceCommitRegex().IsMatch(report),
                "A tracked report must name its full source commit.");
            Assert.IsTrue(ReportWorkflowRunRegex().IsMatch(report),
                "A tracked report must name its workflow run.");
        }
    }

    [TestMethod]
    public void PrivateContentPatterns_RejectFixturesAndAcceptPortableInputs()
    {
        var rejected = new[]
        {
            string.Concat("C:", @"\Users\fixture\source"), string.Concat("c:", "/users/fixture/source"),
            string.Concat("C:", @"\\Users\\fixture\\source"), string.Concat("/mnt/c/", "Users/fixture/source"),
            "https://" + string.Join('.', "gitea", "fleet", "sticktoitive", "net") + "/team/repo",
            string.Concat("CHON", "KERS"), string.Concat("llama", "box"), string.Concat("~/", "schoen-lab/packages/guide.md")
        };
        var patterns = PrivateContentPatterns();
        foreach (var text in rejected)
        {
            Assert.Contains(pattern => Regex.IsMatch(text, pattern), patterns,
                "A private-content negative control was not detected.");
        }

        foreach (var text in new[]
        {
            "docs/quality-gates.md", "${{ vars.MFTLIB_CI_IMAGE }}",
            "$env:MFTLIB_GITEA_REPOSITORY_URL", "https://forge.example.invalid/team/repo",
            @"C:\Data\test\file.txt", "https://github.com/mtschoen/MFTLib"
        })
        {
            Assert.DoesNotContain(pattern => Regex.IsMatch(text, pattern), patterns,
                $"Portable content was rejected: {text}");
        }
    }

    [TestMethod]
    public void MaintainedTools_ListEveryScript()
    {
        var paths = RepositoryFiles.Git("ls-files", "--cached", "--others",
                "--exclude-standard", "-z", "--", "scripts")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".py", ".cmd", ".bat" };
        var scripts = paths.Where(path => extensions.Contains(Path.GetExtension(path)) ||
            RepositoryFiles.Read(path).StartsWith("#!", StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(scripts, "No scripts found under scripts/.");
        var required = scripts.Concat(new[]
        {
            "SampleProgram.Direct/SampleProgram.Direct.csproj",
            "SampleProgram.Watch/SampleProgram.Watch.csproj"
        }).ToArray();
        var failures = ValidateMaintainedTools(
            RepositoryFiles.Read("docs/maintained-developer-tools.md"), required,
            path => File.Exists(Path.Combine(RepositoryFiles.Root, path)));
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        Assert.Contains("(docs/maintained-developer-tools.md)", ReadInstructions());
    }

    [TestMethod]
    public void MaintainedTools_RejectInvalidInventory()
    {
        const string valid = "| `scripts/fixture.ps1` | Parse fixtures | agent | Syntax gate |\n";
        var required = new[] { "scripts/fixture.ps1" };
        Assert.IsEmpty(ValidateMaintainedTools(valid, required, _ => true));
        var cases = new[]
        {
            (Text: "", Required: required, Exists: true, Error: "Missing maintained tool"),
            (Text: valid, Required: ["scripts/nested/new.sh"], Exists: true,
                Error: "Missing maintained tool: scripts/nested/new.sh"),
            (Text: valid + valid, Required: required, Exists: true, Error: "Duplicate maintained tool"),
            (Text: valid.Replace("Parse fixtures", ""), Required: required, Exists: true,
                Error: "Incomplete maintained tool"),
            (Text: valid.Replace("agent", ""), Required: required, Exists: true,
                Error: "Incomplete maintained tool"),
            (Text: valid.Replace("Syntax gate", ""), Required: required, Exists: true,
                Error: "Incomplete maintained tool"),
            (Text: valid, Required: required, Exists: false, Error: "Missing tool path"),
            (Text: "| `scripts/fixture.ps1` | purpose | agent |\n", Required: required,
                Exists: true, Error: "Malformed maintained tool row"),
            (Text: "| `scripts/fixture.ps1 | purpose | agent | Syntax gate |\n", Required: required,
                Exists: true, Error: "Malformed maintained tool path")
        };
        foreach (var item in cases)
        {
            var failures = ValidateMaintainedTools(item.Text, item.Required, _ => item.Exists);
            Assert.Contains(message => message.Contains(item.Error, StringComparison.Ordinal), failures,
                $"Expected {item.Error}: {string.Join(';', failures)}");
        }
    }

    static string[] ValidateMaintainedTools(string text, IEnumerable<string> required,
        Func<string, bool> exists)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("| `", StringComparison.Ordinal)) { continue; }
            var cells = line.Trim().Split('|');
            if (cells.Length != 6)
            {
                failures.Add($"Malformed maintained tool row: {line}");
                continue;
            }
            var name = cells[1].Trim();
            if (name.Length < 3 || name[0] != '`' || name[^1] != '`')
            {
                failures.Add($"Malformed maintained tool path: {name}");
                continue;
            }
            var path = name[1..^1];
            if (!seen.Add(path)) { failures.Add($"Duplicate maintained tool: {path}"); }
            if (!exists(path)) { failures.Add($"Missing tool path: {path}"); }
            if (cells.Skip(2).Take(3).Any(string.IsNullOrWhiteSpace))
            {
                failures.Add($"Incomplete maintained tool: {path}");
            }
        }
        foreach (var path in required)
        {
            if (!seen.Contains(path)) { failures.Add($"Missing maintained tool: {path}"); }
        }
        return [.. failures];
    }

    static string ReadInstructions() =>
        new UTF8Encoding(false, true).GetString(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "repository-docs", "AGENTS.md")));

    static int CountCharacters(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    [GeneratedRegex(@"\r?\n[\t ]*\r?\n")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"\]\((docs/[A-Za-z0-9_.-]+\.md)\)")]
    private static partial Regex LinkedDocsRegex();

    [GeneratedRegex(@"(?m)^Source commit: [0-9a-f]{40}\r?$")]
    private static partial Regex ReportSourceCommitRegex();

    [GeneratedRegex(@"(?m)^Workflow run: \d+\r?$")]
    private static partial Regex ReportWorkflowRunRegex();
}
