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
/// carries the nationality and clearance claims and, since the profile page (design.md
/// §6.2, 2026-09-03), every configured selector claim (§21.15). It feeds two readers: the
/// admin roster, and the profile page every signed-in user can open, which renders the
/// clearance and selector eligibility recorded at the subject's last request. It is
/// <b>never</b> read for an authorization decision: the rule engine, the clearance gate
/// and the selector gate all build their Principal from the token, per request, so a
/// stale mirror can never widen anyone's access — and the profile page reads the mirror
/// through the same gate functions, over a Principal rebuilt from this JSON
/// (<see cref="MirroredPrincipal"/>), so what it shows is what the gate would have
/// decided from those claims, never a second implementation of the rules.</para>
/// </summary>
public sealed class JitUserProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        RocketWikiDbContext db,
        IActingUserAccessor actingUser,
        KnownGroupRecorder knownGroups,
        SelectorCatalog catalog)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst("sub")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrEmpty(subject))
            {
                actingUser.ActingUserId = await UpsertUserAsync(db, context.User, subject, catalog, context.RequestAborted);

                // design.md §6.6's "accumulated from observed logins" — the rule builder's
                // group picker, and the only thing on this instance that can learn a group
                // name nobody has written a rule for yet. AFTER the user upsert's save, in
                // its own unit of work, so picker bookkeeping can never fail a login; see
                // KnownGroupRecorder for the cost model and why a losing race is swallowed.
                // Never read back for an authorization decision (§6.1) — the Principal is
                // still built from the token, every request.
                await knownGroups.RecordAsync(
                    db, context.User.FindAll("groups").Select(c => c.Value).ToList(), context.RequestAborted);
            }
        }

        await next(context);
    }

    private static async Task<Guid> UpsertUserAsync(
        RocketWikiDbContext db, ClaimsPrincipal principal, string subject, SelectorCatalog catalog, CancellationToken ct)
    {
        var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
        var displayName = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value
            ?? subject;

        var attributesJson = JsonSerializer.Serialize(MirrorAttributes(principal, catalog));

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
    /// The claims the mirror records, and ONLY those: the two registered attributes
    /// (design.md §6.2 — nationality and, since §21, clearance) plus every claim the
    /// configured selector catalog names (§21.15). The same allowlist
    /// <see cref="PrincipalBuilder"/> maps from the token, taken from the same singleton
    /// catalog, so the mirror and the principal cannot disagree about which claims exist.
    /// A claim nobody configured is not recorded — the mirror is not "the token, saved".
    ///
    /// <para><b>Raw values, not derived answers.</b> A selector claim is stored as the
    /// literal values the token carried (<c>yes</c>, <c>Yes </c>, <c>no</c>, …), never as
    /// an "eligible" boolean, and the clearance as the literal claim value, never as a
    /// resolved level. Eligibility and clearance are derived at READ time by the gates'
    /// own resolution functions over <see cref="MirroredPrincipal"/>, against the catalog
    /// current at that moment — so a category reconfigured after the user's last sign-in
    /// reads correctly, and the mirror never holds a decision that could go stale on its
    /// own. Every configured key is present (an empty list when the token had no such
    /// claim), matching how nationality and clearance have always been recorded;
    /// <see cref="MirroredPrincipal"/> reads an empty list as "absent", exactly as the
    /// principal builder treats a missing claim.</para>
    /// </summary>
    private static Dictionary<string, string[]> MirrorAttributes(ClaimsPrincipal principal, SelectorCatalog catalog)
    {
        var attributes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [ClearanceGate.NationalityAttributeKey] = ClaimValues(principal, ClearanceGate.NationalityAttributeKey),
            [ClearanceGate.ClearanceAttributeKey] = ClaimValues(principal, ClearanceGate.ClearanceAttributeKey),
        };

        // The catalog already refused a claim name that collides with the two above
        // (SelectorCatalog.ReservedClaimNames), so nothing here can overwrite them.
        foreach (var claimName in catalog.ClaimNames)
        {
            attributes[claimName] = ClaimValues(principal, claimName);
        }

        return attributes;
    }

    private static string[] ClaimValues(ClaimsPrincipal principal, string claimName) =>
        principal.FindAll(claimName).Select(c => c.Value).ToArray();
}
