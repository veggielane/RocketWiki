using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

public sealed class AuditEventFieldResolvers
{
    public async Task<string?> GetUserDisplayNameAsync(
        [Parent] AuditEvent auditEvent,
        [Service] RocketWikiDbContext db,
        CancellationToken cancellationToken)
    {
        if (auditEvent.UserId is null)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == auditEvent.UserId.Value, cancellationToken);
        return user?.DisplayName;
    }
}
