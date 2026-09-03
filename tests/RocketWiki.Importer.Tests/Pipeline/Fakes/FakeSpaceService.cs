using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Importer.Tests.Pipeline.Fakes;

/// <summary>Exercises only what ConfluenceSpaceImporter actually calls (CreateAsync) — everything else throws, so an accidental call is a loud test failure, not a silent no-op.</summary>
public sealed class FakeSpaceService : ISpaceService
{
    public List<CreateSpaceRequest> CreateCalls { get; } = [];

    public List<IReadOnlyList<InitialGrant>> InitialGrants { get; } = [];

    public Func<CreateSpaceRequest, PageMutationError?>? FailCreateWhen { get; set; }

    public Task<PageMutationResult<Space>> CreateAsync(
        CreateSpaceRequest request, IReadOnlyList<InitialGrant> initialGrants, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        CreateCalls.Add(request);
        InitialGrants.Add(initialGrants);

        var failure = FailCreateWhen?.Invoke(request);
        if (failure is not null)
        {
            return Task.FromResult(PageMutationResult<Space>.Failure(failure));
        }

        var space = new Space
        {
            Key = request.Key,
            Name = request.Name,
            Description = request.Description,
            OriginInstanceId = "test-instance",
            CreatedByUserId = actingUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        return Task.FromResult(PageMutationResult<Space>.Success(space));
    }

    public Task<PageMutationResult<Space>> RenameAsync(RenameSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call RenameAsync.");

    public Task<PageMutationResult<Space>> SetHomepageAsync(SetSpaceHomepageRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call SetHomepageAsync.");

    public Task<PageMutationResult<Space>> SetOwnerAsync(SetSpaceOwnerRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call SetOwnerAsync.");

    public Task<PageMutationResult<Space>> ArchiveAsync(ArchiveSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call ArchiveAsync.");

    public Task<PageMutationResult<Space>> RestoreAsync(RestoreSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call RestoreAsync.");

    public Task<PageMutationResult<Space>> SetExportedAsync(SetSpaceExportedRequest request, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call SetExportedAsync.");
}
