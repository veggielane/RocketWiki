using System.Text.Json;
using RocketWiki.Core.Search;
using Xunit;

namespace RocketWiki.Core.Tests.Search;

/// <summary>
/// design.md §9: "both sides therefore test against a shared fixture corpus."
/// This is the backend half of the cross-language anchor contract: every fixture
/// in tests/fixtures/heading-anchors/ (regenerated from the canonical TypeScript
/// implementation on every frontend test run — see the corpus README) must
/// produce byte-identical anchors from the C# port. A failure here means the two
/// slugifiers have diverged and every affected search deep link would silently
/// land on the wrong section — fix the PORT to match the TypeScript, or change
/// the TypeScript first and let the regenerated corpus flow through.
/// </summary>
public class HeadingAnchorsCorpusTests
{
    private sealed record CorpusHeading(int Level, string Text);

    private sealed record CorpusCase(List<CorpusHeading> Headings, List<string> Anchors);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(CorpusDirectory(), "*.json").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Anchors_MatchTheCanonicalTypeScriptImplementation(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(CorpusDirectory(), fileName));
        var corpusCase = JsonSerializer.Deserialize<CorpusCase>(json, JsonOptions);
        Assert.NotNull(corpusCase);

        var headings = corpusCase.Headings.Select(h => new HeadingInfo(h.Level, h.Text)).ToArray();

        var anchors = HeadingAnchors.ComputeHeadingAnchors(headings);

        Assert.Equal(corpusCase.Anchors, anchors);
    }

    [Fact]
    public void Corpus_IsActuallyPresent_SoTheTheoryAboveIsNotVacuouslyGreen()
    {
        // 15 files at the time of writing; ">= 10" keeps this from breaking on
        // legitimate corpus growth while still catching an empty/moved directory,
        // which would otherwise make the contract test silently test nothing.
        Assert.True(Directory.EnumerateFiles(CorpusDirectory(), "*.json").Count() >= 10,
            $"Expected the shared anchor corpus at '{CorpusDirectory()}' — has it moved?");
    }

    /// <summary>Same repo-root walk as RocketWiki.Api.Tests.RepoRoot (that helper is internal to its own assembly).</summary>
    private static string CorpusDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RocketWiki.sln")))
            {
                return Path.Combine(dir.FullName, "tests", "fixtures", "heading-anchors");
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            $"Could not locate RocketWiki.sln by walking up from '{AppContext.BaseDirectory}'.");
    }
}
