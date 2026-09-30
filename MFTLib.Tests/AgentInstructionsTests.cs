using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class AgentInstructionsTests
{
    [TestMethod]
    public void WholeDocument_RemainsBelowCharacterBudget()
    {
        var text = ReadInstructions();
        var characters = CountCharacters(text);
        Assert.IsTrue(characters < 15000,
            $"AGENTS.md has {characters} characters; it must stay below 15000. " +
            "Move narrative unchanged into linked docs pages.");
    }

    [TestMethod]
    public void EveryBlock_RemainsBelowCharacterBudget()
    {
        var blocks = Regex.Split(ReadInstructions(), @"\r?\n[\t ]*\r?\n");
        var longest = blocks.Max(CountCharacters);
        Assert.IsTrue(longest < 3000,
            $"AGENTS.md has a {longest}-character block; each must stay below 3000. " +
            "Separate concise rules with blank lines.");
    }

    [TestMethod]
    public void EveryLinkedDocsPage_Exists()
    {
        var links = Regex.Matches(ReadInstructions(), @"\]\((docs/[A-Za-z0-9_.-]+\.md)\)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.IsTrue(links.Count > 0, "AGENTS.md links no docs pages.");
        foreach (var link in links)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "repository-docs", link);
            Assert.IsTrue(File.Exists(path), $"AGENTS.md links {link}, which does not exist.");
        }
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
}
