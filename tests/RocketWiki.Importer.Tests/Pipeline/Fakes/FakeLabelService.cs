using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Importer.Tests.Pipeline.Fakes;

public sealed class FakeLabelService : ILabelService
{
    private readonly Dictionary<(Guid SpaceId, string Name), Label> _labelsByName = new();
    private readonly HashSet<(Guid PageId, Guid LabelId)> _attachments = [];

    public List<CreateLabelRequest> CreateCalls { get; } = [];

    public List<AttachLabelRequest> AttachCalls { get; } = [];

    public Func<CreateLabelRequest, PageMutationError?>? FailCreateWhen { get; set; }

    public Func<AttachLabelRequest, PageMutationError?>? FailAttachWhen { get; set; }

    public Task<PageMutationResult<Label>> CreateLabelAsync(
        CreateLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        CreateCalls.Add(request);

        if (_labelsByName.ContainsKey((request.SpaceId, request.Name)))
        {
            return Task.FromResult(PageMutationResult<Label>.Failure(new ValidationError($"Label '{request.Name}' already exists in this space.")));
        }

        var failure = FailCreateWhen?.Invoke(request);
        if (failure is not null)
        {
            return Task.FromResult(PageMutationResult<Label>.Failure(failure));
        }

        var label = new Label { SpaceId = request.SpaceId, Name = request.Name };
        _labelsByName[(request.SpaceId, request.Name)] = label;
        return Task.FromResult(PageMutationResult<Label>.Success(label));
    }

    public Task<PageMutationResult<PageLabel>> AttachLabelAsync(
        AttachLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        AttachCalls.Add(request);

        if (!_attachments.Add((request.PageId, request.LabelId)))
        {
            return Task.FromResult(PageMutationResult<PageLabel>.Failure(new ValidationError("Label is already attached to this page.")));
        }

        var failure = FailAttachWhen?.Invoke(request);
        if (failure is not null)
        {
            _attachments.Remove((request.PageId, request.LabelId));
            return Task.FromResult(PageMutationResult<PageLabel>.Failure(failure));
        }

        return Task.FromResult(PageMutationResult<PageLabel>.Success(new PageLabel { PageId = request.PageId, LabelId = request.LabelId }));
    }

    public Task<PageMutationResult<Guid>> DetachLabelAsync(DetachLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call DetachLabelAsync.");

    public Task<IReadOnlyList<Page>> GetPagesByLabelAsync(Guid spaceId, string labelName, Principal principal, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call GetPagesByLabelAsync.");
}
