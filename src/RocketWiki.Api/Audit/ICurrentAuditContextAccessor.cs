using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;

namespace RocketWiki.Api.Audit;

/// <summary>
/// The <see cref="AuditContext"/> for the current request (design.md §7: request id,
/// client IP, channel), computed fresh on every read from <see cref="HttpContext"/> —
/// deliberately NOT precomputed by a middleware and stashed on
/// <c>RocketWikiDbContext.AuditContext</c> for resolvers to read back. That was the
/// original design here, and it doesn't work: Hot Chocolate executes resolvers in a
/// different DI scope than the ASP.NET Core middleware pipeline uses for
/// <c>app.UseMiddleware&lt;T&gt;()</c>, confirmed by instrumenting both sites - the
/// resolver's injected `RocketWikiDbContext` is a *different instance* than the one
/// any middleware touched, so anything set on the middleware's copy is invisible
/// from inside a resolver. This accessor sidesteps the problem entirely by never
/// relying on cross-scope instance state: like <see cref="RocketWiki.Api.Identity.ICurrentPrincipalAccessor"/>,
/// it derives everything fresh from <see cref="IHttpContextAccessor"/>, which
/// (unlike arbitrary scoped services) is reliably the same HttpContext on both
/// sides of that boundary.
///
/// Callers that need to raise a domain event still set
/// <c>RocketWikiDbContext.AuditContext</c> themselves, using <see cref="Current"/> -
/// see <c>IPageService</c>'s explicit <c>auditContext</c> parameter, filled from here
/// by the mutation resolvers in <c>Mutation.cs</c>.
/// </summary>
public interface ICurrentAuditContextAccessor
{
    /// <summary>Null for a request whose path maps to no known channel (health
    /// checks, etc.) - see <see cref="CurrentAuditContextAccessor.DetermineChannel"/>.</summary>
    AuditContext? Current { get; }
}

public sealed class CurrentAuditContextAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentAuditContextAccessor
{
    public AuditContext? Current
    {
        get
        {
            var context = httpContextAccessor.HttpContext;
            if (context is null)
            {
                return null;
            }

            var channel = DetermineChannel(context.Request.Path);
            if (channel is null)
            {
                return null;
            }

            return new AuditContext(
                channel.Value,
                context.TraceIdentifier,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }
    }

    /// <summary>internal + InternalsVisibleTo(RocketWiki.Api.Tests) so this pure
    /// mapping is unit-testable directly, without needing a live HTTP round trip
    /// per path.</summary>
    internal static AuditChannel? DetermineChannel(PathString path)
    {
        if (path.StartsWithSegments("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            return AuditChannel.GraphQl;
        }

        if (path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return AuditChannel.Mcp;
        }

        if (path.StartsWithSegments("/attachments", StringComparison.OrdinalIgnoreCase))
        {
            return AuditChannel.Attachment;
        }

        return null;
    }
}
