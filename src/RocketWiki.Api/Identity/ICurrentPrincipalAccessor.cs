using RocketWiki.Core.Access;

namespace RocketWiki.Api.Identity;

/// <summary>
/// The ABAC <see cref="Principal"/> for the current request, built fresh from the
/// validated token on every access (design.md §6.1) — this is what
/// <c>IPageReadService</c>/<c>IPageService</c> take for every authorization decision.
///
/// Deliberately separate from <see cref="IActingUserAccessor"/>: that one is the local
/// `User.Id` mirror, used only for display and foreign keys. **Never substitute one for
/// the other** — a permission decision must always go through this accessor (the token),
/// never the mirrored row, or a change in Keycloak stops taking effect until next login
/// (design.md §6.1's whole point).
/// </summary>
public interface ICurrentPrincipalAccessor
{
    /// <summary>Null when the request is unauthenticated or the token carries no usable
    /// subject claim — both cases every caller must treat as "cannot view anything"
    /// (design.md: no anonymous wikis), never as "everyone"/"nobody in particular".</summary>
    Principal? Current { get; }
}

/// <summary>Builds through the one <see cref="PrincipalBuilder"/> the hub also uses, so
/// the HTTP path maps exactly the fixed claim list (<c>groups</c>, <c>nationality</c>)
/// and nothing the hub path would not.</summary>
public sealed class CurrentPrincipalAccessor(IHttpContextAccessor httpContextAccessor)
    : ICurrentPrincipalAccessor
{
    private Principal? _cached;
    private bool _computed;

    public Principal? Current
    {
        get
        {
            if (!_computed)
            {
                _cached = PrincipalBuilder.Build(httpContextAccessor.HttpContext?.User);
                _computed = true;
            }

            return _cached;
        }
    }
}
