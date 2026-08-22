namespace RocketWiki.Core.Services;

public sealed record CreateLabelRequest(Guid SpaceId, string Name);

public sealed record AttachLabelRequest(Guid PageId, Guid LabelId);

public sealed record DetachLabelRequest(Guid PageId, Guid LabelId);
