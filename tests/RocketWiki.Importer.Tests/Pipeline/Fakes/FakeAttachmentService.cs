using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Importer.Tests.Pipeline.Fakes;

public sealed class FakeAttachmentService : IAttachmentService
{
    public List<(UploadAttachmentRequest Request, byte[] Bytes)> UploadCalls { get; } = [];

    public Func<UploadAttachmentRequest, PageMutationError?>? FailUploadWhen { get; set; }

    public async Task<PageMutationResult<Attachment>> UploadAsync(
        UploadAttachmentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        UploadCalls.Add((request, bytes));

        var failure = FailUploadWhen?.Invoke(request);
        if (failure is not null)
        {
            return PageMutationResult<Attachment>.Failure(failure);
        }

        return PageMutationResult<Attachment>.Success(new Attachment
        {
            PageId = request.PageId,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = bytes.Length,
            UploadedByUserId = actingUserId,
            CreatedAtUtc = DateTime.UtcNow,
        });
    }

    public Task<PageMutationResult<Attachment>> DeleteAsync(DeleteAttachmentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call DeleteAsync.");
}
