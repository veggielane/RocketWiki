using RocketWiki.Importer.Pipeline;

namespace RocketWiki.Importer.Tests.Pipeline;

public class TwoPassPageIdResolverTests
{
    [Fact]
    public void Page_registered_by_content_id_resolves_by_content_id()
    {
        var resolver = new TwoPassPageIdResolver();
        resolver.RegisterPage("ENG", "1001", "Runbook", "page-guid-1");

        Assert.True(resolver.TryResolvePage(new ConfluencePageReference("ENG", null, "1001"), out var id));
        Assert.Equal("page-guid-1", id);
    }

    [Fact]
    public void Page_registered_by_content_id_also_resolves_by_title()
    {
        var resolver = new TwoPassPageIdResolver();
        resolver.RegisterPage("ENG", "1001", "Runbook", "page-guid-1");

        Assert.True(resolver.TryResolvePage(new ConfluencePageReference("ENG", "Runbook", null), out var id));
        Assert.Equal("page-guid-1", id);
    }

    [Fact]
    public void Unregistered_page_reference_fails_to_resolve()
    {
        var resolver = new TwoPassPageIdResolver();

        Assert.False(resolver.TryResolvePage(new ConfluencePageReference("ENG", "Nonexistent", null), out _));
    }

    [Fact]
    public void Attachment_resolves_by_owning_page_content_id_and_filename()
    {
        var resolver = new TwoPassPageIdResolver();
        resolver.RegisterAttachment("ENG", "1001", "Runbook", "diagram.png", "attach-guid-1");

        Assert.True(resolver.TryResolveAttachment(new ConfluenceAttachmentReference("ENG", null, "1001", "diagram.png"), out var id));
        Assert.Equal("attach-guid-1", id);
    }

    [Fact]
    public void Attachment_also_resolves_by_owning_page_title_when_content_id_is_absent()
    {
        var resolver = new TwoPassPageIdResolver();
        resolver.RegisterAttachment("ENG", "1001", "Runbook", "diagram.png", "attach-guid-1");

        Assert.True(resolver.TryResolveAttachment(new ConfluenceAttachmentReference("ENG", "Runbook", null, "diagram.png"), out var id));
        Assert.Equal("attach-guid-1", id);
    }

    [Fact]
    public void Attachment_with_the_right_page_but_wrong_filename_does_not_resolve()
    {
        var resolver = new TwoPassPageIdResolver();
        resolver.RegisterAttachment("ENG", "1001", "Runbook", "diagram.png", "attach-guid-1");

        Assert.False(resolver.TryResolveAttachment(new ConfluenceAttachmentReference("ENG", null, "1001", "other.png"), out _));
    }
}
