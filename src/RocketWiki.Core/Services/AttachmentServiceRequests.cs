namespace RocketWiki.Core.Services;

/// <summary>Content is read once to compute ContentHash and to write to storage - callers should not reuse the stream afterward.</summary>
public sealed record UploadAttachmentRequest(Guid PageId, string FileName, string ContentType, Stream Content);

public sealed record DeleteAttachmentRequest(Guid AttachmentId);
