using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Importer.Tests.Pipeline.Fakes;

/// <summary>
/// A minimal in-memory stand-in for IPageService that enforces the two invariants the
/// importer's tree-building logic depends on — a page can't be created under a parent
/// that doesn't exist yet, and a slug must be unique among siblings — so a test that gets
/// the creation order or slug disambiguation wrong fails here, the same way it would
/// against the real EF-backed service.
/// </summary>
public sealed class FakePageService : IPageService
{
    private readonly Dictionary<Guid, Page> _pages = [];

    public List<CreatePageRequest> CreateCalls { get; } = [];

    public List<UpdatePageContentRequest> UpdateCalls { get; } = [];

    public Func<CreatePageRequest, PageMutationError?>? FailCreateWhen { get; set; }

    public Func<UpdatePageContentRequest, PageMutationError?>? FailUpdateWhen { get; set; }

    public Page? GetCreatedPage(Guid id) => _pages.GetValueOrDefault(id);

    public IReadOnlyCollection<Page> CreatedPages => _pages.Values;

    public Task<PageMutationResult<Page>> CreatePageAsync(
        CreatePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        CreateCalls.Add(request);

        if (request.ParentPageId is { } parentId && !_pages.ContainsKey(parentId))
        {
            return Task.FromResult(PageMutationResult<Page>.Failure(new NotFoundError(parentId)));
        }

        if (_pages.Values.Any(p => p.SpaceId == request.SpaceId && p.ParentPageId == request.ParentPageId && p.Slug == request.Slug))
        {
            return Task.FromResult(PageMutationResult<Page>.Failure(new ValidationError($"Slug '{request.Slug}' is already used by a sibling page.")));
        }

        var failure = FailCreateWhen?.Invoke(request);
        if (failure is not null)
        {
            return Task.FromResult(PageMutationResult<Page>.Failure(failure));
        }

        var page = new Page
        {
            SpaceId = request.SpaceId,
            ParentPageId = request.ParentPageId,
            Slug = request.Slug,
            Title = request.Title,
            CurrentContent = request.Content,
            CurrentRevisionNumber = 1,
        };
        _pages[page.Id] = page;
        return Task.FromResult(PageMutationResult<Page>.Success(page));
    }

    public Task<PageMutationResult<Page>> UpdatePageContentAsync(
        UpdatePageContentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        UpdateCalls.Add(request);

        if (!_pages.TryGetValue(request.PageId, out var page))
        {
            return Task.FromResult(PageMutationResult<Page>.Failure(new NotFoundError(request.PageId)));
        }

        if (request.ExpectedRevisionNumber != page.CurrentRevisionNumber)
        {
            return Task.FromResult(PageMutationResult<Page>.Failure(
                new StaleRevisionError(request.ExpectedRevisionNumber, page.CurrentRevisionNumber, page.Title, page.CurrentContent)));
        }

        var failure = FailUpdateWhen?.Invoke(request);
        if (failure is not null)
        {
            return Task.FromResult(PageMutationResult<Page>.Failure(failure));
        }

        page.Title = request.Title;
        page.CurrentContent = request.Content;
        page.CurrentRevisionNumber++;
        return Task.FromResult(PageMutationResult<Page>.Success(page));
    }

    public Task<PageMutationResult<Page>> MovePageAsync(MovePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call MovePageAsync.");

    public Task<PageMutationResult<PageDeleteSummary>> DeletePageAsync(DeletePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call DeletePageAsync.");

    public Task<PageMutationResult<PageRestoreSummary>> RestorePageAsync(RestorePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call RestorePageAsync.");

    public Task<PageMutationResult<Page>> RestoreRevisionAsync(RestoreRevisionRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call RestoreRevisionAsync.");
}
