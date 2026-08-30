using System.Runtime.CompilerServices;
using System.Text;

namespace RocketWiki.Importer.Tests.Corpus;

/// <summary>
/// design.md §13: regenerates tests/fixtures/converted-markdown/{name}.md from
/// <see cref="ConversionCorpus"/> on every run and asserts basic sanity on the result.
/// This is deliberately a "regenerate, don't hand-maintain" test, not a byte-for-byte
/// golden-file diff — the corpus's job is to always reflect the converter's *current*
/// output so the editor's round-trip suite is testing reality, not a snapshot someone
/// forgot to update. The assertions here catch a broken generation (empty output, leaked
/// raw Confluence markup, malformed tables), not "the converter's behavior changed."
/// </summary>
public class CorpusGenerationTests
{
    [Theory]
    [MemberData(nameof(ConversionCorpus.AsTheoryData), MemberType = typeof(ConversionCorpus))]
    public void Fixture_regenerates_a_sane_non_empty_corpus_file(CorpusFixture fixture)
    {
        var resolver = new FakePageIdResolver();
        fixture.ConfigureResolver?.Invoke(resolver);
        var converter = new ConfluenceStorageConverter(resolver);
        var context = fixture.Context ?? new ConfluencePageContext("ENG", fixture.Name, ContentId: null);

        var result = converter.Convert(fixture.Xhtml, context);

        Assert.False(string.IsNullOrWhiteSpace(result.Markdown), $"Fixture '{fixture.Name}' produced empty Markdown - it doesn't belong in a round-trip corpus.");
        Assert.DoesNotContain("<ac:", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<ri:", result.Markdown, StringComparison.Ordinal);
        Assert.EndsWith("\n", result.Markdown, StringComparison.Ordinal);
        AssertConsistentTableColumnCounts(fixture.Name, result.Markdown);

        // Regenerated into the WORKING TREE on purpose: drift is enforced by CI failing
        // on a dirty worktree under the corpus directory (.github/workflows/ci.yml), not
        // by an assertion here. Which is why there is no assertion here any more — this
        // used to end with Assert.Equal(result.Markdown, File.ReadAllText(path)),
        // immediately after writing that exact string to that exact path. It asserted
        // that the filesystem round-trips a string, and could not fail. The assertions
        // that CAN fail are the shape checks above; the drift check is CI’s.
        var path = Path.Combine(GetCorpusDirectory(), fixture.Name + ".md");
        File.WriteAllText(path, result.Markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    [Fact]
    public void Fixture_names_are_unique_so_no_corpus_file_is_silently_overwritten_by_another_fixture()
    {
        var duplicates = ConversionCorpus.Fixtures
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Corpus_directory_contains_no_stale_files_left_over_from_renamed_or_removed_fixtures()
    {
        var expectedFiles = ConversionCorpus.Fixtures.Select(f => f.Name + ".md").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory.EnumerateFiles(GetCorpusDirectory(), "*.md").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var stale = actualFiles.Except(expectedFiles).ToList();
        Assert.True(stale.Count == 0, $"Stale corpus file(s) with no matching fixture (delete or rename the fixture that used to produce them): {string.Join(", ", stale)}");
    }

    /// <summary>Every pipe-table row in the document should have the same number of '|' separators as
    /// the first — a cheap structural sanity check independent of the converter's own table tests.
    /// Escaped pipes ("\|") are cell text, not separators, so they are stripped before counting; the
    /// count stays consistent under colspan merges too, because an adjacent-pipe merge contributes
    /// exactly as many pipes as the cells it replaces.</summary>
    private static void AssertConsistentTableColumnCounts(string fixtureName, string markdown)
    {
        var pipeRowPipeCounts = markdown
            .Split('\n')
            .Where(line => line.StartsWith('|'))
            .Select(line => line.Replace("\\|", string.Empty).Count(c => c == '|'))
            .ToList();

        if (pipeRowPipeCounts.Count == 0)
        {
            return;
        }

        var expected = pipeRowPipeCounts[0];
        Assert.True(pipeRowPipeCounts.All(c => c == expected), $"Fixture '{fixtureName}' produced a table with inconsistent column counts across rows.");
    }

    private static string GetCorpusDirectory([CallerFilePath] string sourceFile = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFile)!; // .../tests/RocketWiki.Importer.Tests/Corpus
        var repoRoot = Path.GetFullPath(Path.Combine(sourceDirectory, "..", "..", ".."));
        var corpusDirectory = Path.Combine(repoRoot, "tests", "fixtures", "converted-markdown");
        Directory.CreateDirectory(corpusDirectory);
        return corpusDirectory;
    }
}
