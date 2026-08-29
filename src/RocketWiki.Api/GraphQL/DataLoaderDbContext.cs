using Microsoft.EntityFrameworkCore;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// A short-lived <see cref="RocketWikiDbContext"/> for one DataLoader batch.
///
/// <para>DataLoaders must NOT share the request-scoped context. Hot Chocolate resolves
/// sibling fields in parallel, so several loaders dispatch their batches at the same
/// moment; on one <c>DbContext</c> that is a second operation started before the first
/// completed, and EF Core throws. The executor reports it as an "Unexpected Execution
/// Error" per field, which is what a `PageById` query degraded into — six fields
/// (<c>canEdit</c>, <c>canComment</c>, <c>canManageAccess</c>, <c>labels</c>,
/// <c>labelDetails</c>, <c>properties</c>) failing together while each one queried on
/// its own was fine, and the SPA showing "Couldn't load this page" for every page.</para>
///
/// <para>Built from the registered <see cref="DbContextOptions{TContext}"/> rather than
/// an <c>IDbContextFactory</c> on purpose: the options object is whatever the host
/// registered, so this follows the provider automatically — SQL Server under Aspire,
/// SQLite in the §14 integration tier — instead of a second registration that would
/// have to duplicate the connection string, the retry policy and
/// <c>UseLocalInstanceId</c>, and would silently diverge the day one of them changed.</para>
///
/// <para>The caller owns disposal: <c>await using</c>, always. These contexts are
/// read-only by construction — a DataLoader batches reads — so none of the audit or
/// domain-event machinery hanging off <c>SaveChanges</c> is involved, and nothing here
/// competes with the request-scoped context that mutations use.</para>
/// </summary>
internal static class DataLoaderDbContext
{
    public static RocketWikiDbContext Create(DbContextOptions<RocketWikiDbContext> options) => new(options);
}
