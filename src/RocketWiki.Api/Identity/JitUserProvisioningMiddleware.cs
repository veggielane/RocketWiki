using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Data;

namespace RocketWiki.Api.Identity;

/// <summary>
/// design.md §11 step 3: on each authenticated request, upsert the local <c>User</c> row
/// from token claims. Purely a mirror for display and foreign keys (design.md §6.1) — the
/// row this writes is never read back for an authorization decision, only its <c>Id</c>,
/// exposed via <see cref="IActingUserAccessor"/> for audit attribution and for services
/// that need an <c>actingUserId</c> to stamp on what they write.
/// </summary>
public sealed class JitUserProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RocketWikiDbContext db, IActingUserAccessor actingUser)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst("sub")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrEmpty(subject))
            {
                actingUser.ActingUserId = await UpsertUserAsync(db, context.User, subject, context.RequestAborted);
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

        // Registered attributes only (design.md §6.2) — nationality is the only one wired
        // end to end today. Admin-display only; the rule engine builds its Principal
        // straight from the token (design.md §6.1), never from this JSON blob.
        var nationality = principal.FindAll("nationality").Select(c => c.Value).ToArray();
        var attributesJson = JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["nationality"] = nationality,
        });

        var now = DateTime.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject, ct);

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
            user.Email = email;
            user.DisplayName = displayName;
            user.AttributesJson = attributesJson;
            user.LastSeenAtUtc = now;
        }

        // JIT provisioning is infrastructure bookkeeping, not a user-facing mutation — it
        // deliberately does not go through RaiseDomainEvent/IAuditSink (design.md §7 audits
        // user actions, not the plumbing that recognizes who the user is). SaveChangesAsync
        // is safe here regardless of whether AuditContext has been set yet: no domain event
        // is pending, so RocketWikiDbContext's override is a no-op beyond the normal save.
        await db.SaveChangesAsync(ct);

        return user.Id;
    }
}
