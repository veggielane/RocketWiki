using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Access;
using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using RocketWiki.Data;

namespace RocketWiki.Api.Identity;

/// <summary>
/// design.md §11 step 3: on each authenticated request, upsert the local <c>User</c> row
/// from token claims. Purely a mirror for display and foreign keys (design.md §6.1) — the
/// row this writes is never read back for an authorization decision, only its <c>Id</c>,
/// exposed via <see cref="IActingUserAccessor"/> for audit attribution and for services
/// that need an <c>actingUserId</c> to stamp on what they write.
///
/// <para><b>What the attribute mirror is for, and what it is not.</b> <c>AttributesJson</c>
/// carries two claims: <c>nationality</c>, for the admin roster (§6.2), and — since the
/// profile page changed shape (§6.2) — <c>groups</c>, the memberships recorded at the
/// subject's last request, which the profile page every signed-in user can open renders
/// as they were mirrored. It used to carry the clearance claim and every configured
/// selector claim too, so the profile could re-run the clearance and eligibility gates
/// over them; those gates are gone (this deployment carries neither attribute in
/// Keycloak), so those claims are no longer recorded and there is nothing to re-run.
/// The mirror is <b>never</b> read for an authorization decision: the rule engine and
/// the marking gate build their Principal from the token, per request, so a stale mirror
/// can never widen anyone's access — and the profile page reads the mirrored group list
/// as a recorded fact about a mirror, never as an input to any gate.</para>
/// </summary>
public sealed class JitUserProvisioningMiddleware(RequestDelegate next)
{
    /// <summary>The claim the mirror records group membership from — the same
    /// <c>groups</c> claim <see cref="PrincipalBuilder"/> builds the Principal's groups
    /// from, so what the profile shows is what the gates were handed.</summary>
    public const string GroupsClaimName = "groups";

    public async Task InvokeAsync(
        HttpContext context,
        RocketWikiDbContext db,
        IActingUserAccessor actingUser,
        KnownGroupRecorder knownGroups)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst("sub")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrEmpty(subject))
            {
                actingUser.ActingUserId = await UpsertUserAsync(db, context.User, subject, context.RequestAborted);

                // design.md §6.6's "accumulated from observed logins" — the rule builder's
                // group picker, and the only thing on this instance that can learn a group
                // name nobody has written a rule for yet. AFTER the user upsert's save, in
                // its own unit of work, so picker bookkeeping can never fail a login; see
                // KnownGroupRecorder for the cost model and why a losing race is swallowed.
                // Never read back for an authorization decision (§6.1) — the Principal is
                // still built from the token, every request.
                await knownGroups.RecordAsync(
                    db, context.User.FindAll(GroupsClaimName).Select(c => c.Value).ToList(), context.RequestAborted);
            }
        }

        await next(context);
    }

    private static async Task<Guid> UpsertUserAsync(
        RocketWikiDbContext db, ClaimsPrincipal principal, string subject, CancellationToken ct)
    {
        var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
        var displayName = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value
            ?? subject;

        var attributesJson = JsonSerializer.Serialize(MirrorAttributes(principal));

        var now = DateTime.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject, ct);
        var created = user is null;

        if (user is null)
        {
            user = new User
            {
                Subject = subject,
                Email = email,
                DisplayName = displayName,
                AttributesJson = attributesJson,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
            };
            db.Users.Add(user);
        }
        else
        {
            // Email drift (design.md §11 step 3 refreshes the mirror per login): the
            // stored avatar email hashes are derived from this exact column, and a
            // stale hash makes the Gravatar endpoint serve the OLD address's avatar
            // for whoever holds that address next - a correctness bug, not cosmetics.
            // Recomputed here, in the same upsert that changes the email, because this
            // is the only writer of User.Email for real (non-shadow) users; the
            // avatar-row query runs only when the email actually changed, so the
            // steady-state request cost is zero. Like the upsert itself, this is
            // mirror bookkeeping, not a user action - no domain event, no audit row.
            var emailChanged = !string.Equals(user.Email, email, StringComparison.Ordinal);

            user.Email = email;
            user.DisplayName = displayName;
            user.AttributesJson = attributesJson;
            user.LastSeenAtUtc = now;

            if (emailChanged)
            {
                var avatar = await db.UserAvatars.FirstOrDefaultAsync(a => a.UserId == user.Id, ct);
                if (avatar is not null)
                {
                    (avatar.EmailHashMd5, avatar.EmailHashSha256) = AvatarEmailHasher.Compute(email);
                }
            }
        }

        // JIT provisioning is infrastructure bookkeeping, not a user-facing mutation — it
        // deliberately does not go through RaiseDomainEvent/IAuditSink (design.md §7 audits
        // user actions, not the plumbing that recognizes who the user is). SaveChangesAsync
        // is safe here regardless of whether AuditContext has been set yet: no domain event
        // is pending, so RocketWikiDbContext's override is a no-op beyond the normal save.
        await db.SaveChangesAsync(ct);

        // design.md §15: created-vs-refreshed only. The subject, email, display name and
        // above all the nationality claim this method just serialized are exactly the
        // attribute values §15 forbids in telemetry.
        ApiTelemetry.RecordJitProvisioning(created);

        return user.Id;
    }

    /// <summary>
    /// The claims the mirror records, and ONLY those: <c>nationality</c> (the registered
    /// attribute, design.md §6.2) and <c>groups</c> (for the profile page). The same two
    /// claims <see cref="PrincipalBuilder"/> maps from the token, so the mirror and the
    /// principal cannot disagree about which claims exist. A claim outside that list is
    /// not recorded — the mirror is not "the token, saved".
    ///
    /// <para><b>Raw values, in token order.</b> The group list is stored exactly as the
    /// token carried it, never sorted, de-duplicated or interpreted; the profile page
    /// sorts for display and says "as of your last sign-in" in words. Every key is
    /// present (an empty list when the token had no such claim), matching how nationality
    /// has always been recorded, so a reader can tell "recorded as none" from "never
    /// recorded by this build".</para>
    /// </summary>
    private static Dictionary<string, string[]> MirrorAttributes(ClaimsPrincipal principal) =>
        new(StringComparer.Ordinal)
        {
            [CaveatGate.NationalityAttributeKey] = ClaimValues(principal, CaveatGate.NationalityAttributeKey),
            [GroupsClaimName] = ClaimValues(principal, GroupsClaimName),
        };

    private static string[] ClaimValues(ClaimsPrincipal principal, string claimName) =>
        principal.FindAll(claimName).Select(c => c.Value).ToArray();
}
