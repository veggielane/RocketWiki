using RocketWiki.Importer.Pipeline;

namespace RocketWiki.Importer.Tests.Pipeline;

public class SlugifierTests
{
    [Fact]
    public void Simple_ascii_title_lowercases_and_dashes_whitespace()
    {
        var used = new HashSet<string>();
        Assert.Equal("deployment-guide", Slugifier.Slugify("Deployment Guide", used));
    }

    [Fact]
    public void Diacritics_are_stripped_not_dropped()
    {
        var used = new HashSet<string>();
        Assert.Equal("cafe-menu", Slugifier.Slugify("Café Menü", used));
    }

    [Fact]
    public void Punctuation_collapses_to_a_single_dash_and_trims_the_ends()
    {
        var used = new HashSet<string>();
        Assert.Equal("q-a-faq", Slugifier.Slugify("Q&A -- FAQ!!", used));
    }

    [Fact]
    public void Colliding_titles_among_siblings_get_disambiguated()
    {
        var used = new HashSet<string>();
        Assert.Equal("notes", Slugifier.Slugify("Notes", used));
        Assert.Equal("notes-2", Slugifier.Slugify("Notes", used));
        Assert.Equal("notes-3", Slugifier.Slugify("Notes", used));
    }

    [Fact]
    public void Non_colliding_titles_in_different_sibling_scopes_do_not_disambiguate_each_other()
    {
        var scopeA = new HashSet<string>();
        var scopeB = new HashSet<string>();
        Assert.Equal("notes", Slugifier.Slugify("Notes", scopeA));
        Assert.Equal("notes", Slugifier.Slugify("Notes", scopeB));
    }

    [Fact]
    public void Non_latin_title_falls_back_to_a_stable_content_derived_slug_instead_of_a_generic_one()
    {
        var used = new HashSet<string>();
        var slug1 = Slugifier.Slugify("日本語のタイトル", used);
        var slug2 = Slugifier.Slugify("日本語のタイトル", new HashSet<string>());

        Assert.StartsWith("page-", slug1);
        Assert.Equal(slug1, slug2); // deterministic for the same title
    }

    [Fact]
    public void Two_different_non_latin_titles_do_not_collide_on_the_same_fallback_slug()
    {
        var used = new HashSet<string>();
        var slug1 = Slugifier.Slugify("日本語のタイトル", used);
        var slug2 = Slugifier.Slugify("другой заголовок", used);

        Assert.NotEqual(slug1, slug2);
    }

    [Fact]
    public void Very_long_title_is_truncated_to_a_bounded_slug_length()
    {
        var used = new HashSet<string>();
        var title = string.Concat(Enumerable.Repeat("word ", 60));

        var slug = Slugifier.Slugify(title, used);

        Assert.True(slug.Length <= 150);
    }
}
