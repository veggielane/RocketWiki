using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

public sealed class AuditEventFieldResolvers
{
    /// <summary>Batched via <see cref="UserRefByIdDataLoader"/> — previously a direct
    /// per-row Users query, an N+1 across an audit page. Stays a bare display-name
    /// string (not a <see cref="UserRef"/>) because the field predates the shared
    /// shape and the shipped audit-viewer operation selects it as a String; the id is
    /// already on <c>AuditEvent.userId</c> next to it.</summary>
    public async Task<string?> GetUserDisplayNameAsync(
        [Parent] AuditEvent auditEvent,
        UserRefByIdDataLoader userLoader,
        CancellationToken cancellationToken)
    {
        if (auditEvent.UserId is null)
        {
            return null;
        }

        var user = await userLoader.LoadAsync(auditEvent.UserId.Value, cancellationToken);
        return user?.DisplayName;
    }
}
