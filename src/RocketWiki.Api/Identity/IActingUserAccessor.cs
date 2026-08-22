namespace RocketWiki.Api.Identity;

/// <summary>
/// The local <c>User.Id</c> for the current request's authenticated principal,
/// JIT-provisioned per request by <see cref="JitUserProvisioningMiddleware"/>
/// (design.md §11.3). Null for unauthenticated requests, or any authenticated
/// request whose token carries no usable subject claim.
///
/// This is a display/foreign-key convenience and the id future services take
/// as <c>actingUserId</c> — nothing more. **Authorization always evaluates the
/// token directly via the ABAC <c>Principal</c> (design.md §6.1), never the
/// mirrored `User` row or this accessor.** Don't reach for this where a
/// permission decision is being made.
/// </summary>
public interface IActingUserAccessor
{
    Guid? ActingUserId { get; set; }
}

/// <summary>
/// Backed by <see cref="HttpContext.Items"/> rather than its own scoped instance
/// state, deliberately: Hot Chocolate resolves <c>[Service]</c>-injected custom
/// types from a different DI scope than the ASP.NET Core middleware pipeline uses
/// for classic <c>app.UseMiddleware&lt;T&gt;()</c> registrations (confirmed by
/// instrumenting both sites - two different instances of this class, same
/// request). <c>HttpContext</c> itself, via <see cref="IHttpContextAccessor"/>,
/// is the one thing reliably identical on both sides of that boundary, so this
/// reads/writes through it instead of relying on scoped-service instance
/// identity to match. <see cref="RocketWiki.Api.Identity.CurrentPrincipalAccessor"/>
/// already depended on that same fact working; this makes the dependency explicit
/// rather than accidentally relying on some services (apparently
/// <c>RocketWikiDbContext</c>) getting special-cased scope-sharing that plain
/// custom services don't.
/// </summary>
public sealed class ActingUserAccessor(IHttpContextAccessor httpContextAccessor) : IActingUserAccessor
{
    private const string ItemsKey = "RocketWiki.Api.ActingUserId";

    public Guid? ActingUserId
    {
        get
        {
            var items = httpContextAccessor.HttpContext?.Items;
            return items is not null && items.TryGetValue(ItemsKey, out var value) ? (Guid?)value : null;
        }
        set
        {
            var context = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("No HttpContext available to store the acting user id on.");
            context.Items[ItemsKey] = value;
        }
    }
}
