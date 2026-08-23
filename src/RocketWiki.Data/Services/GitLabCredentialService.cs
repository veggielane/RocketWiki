using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// design.md GitLab integration section: per-user PAT storage. Set/clear flow
/// through the domain-event pipeline (<see cref="GitLabTokenSetEvent"/> /
/// <see cref="GitLabTokenClearedEvent"/>) so the audit row commits with the
/// credential row — no side-door write. The plaintext token exists in process
/// memory only between validation and <see cref="ITokenProtector.Protect"/>;
/// it is never placed on an event, an audit row, an exception message, or a
/// return value of this class other than <see cref="GetDecryptedTokenAsync"/>
/// (whose one consumer is the outbound GitLab fetch path).
/// </summary>
public sealed class GitLabCredentialService(RocketWikiDbContext db, ITokenProtector protector) : IGitLabCredentialService
{
    /// <summary>Generous ceiling over every GitLab token shape (PATs are ~26–50 chars;
    /// group/project tokens similar). Anything longer is not a token.</summary>
    private const int MaxTokenLength = 512;

    public async Task<PageMutationResult<GitLabTokenState>> SetTokenAsync(
        SetGitLabTokenRequest request, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        // Validation messages deliberately never echo the submitted value — a
        // near-miss token in an error message would end up in client logs.
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return PageMutationResult<GitLabTokenState>.Failure(new ValidationError("Token must not be empty."));
        }

        if (token.Length > MaxTokenLength)
        {
            return PageMutationResult<GitLabTokenState>.Failure(
                new ValidationError($"Token exceeds the maximum length of {MaxTokenLength} characters."));
        }

        if (token.Any(char.IsControl))
        {
            return PageMutationResult<GitLabTokenState>.Failure(
                new ValidationError("Token must not contain control characters."));
        }

        var now = DateTime.UtcNow;
        var existing = await db.GitLabCredentials
            .FirstOrDefaultAsync(c => c.UserId == actingUserId, cancellationToken);

        if (existing is null)
        {
            db.GitLabCredentials.Add(new GitLabCredential
            {
                UserId = actingUserId,
                ProtectedToken = protector.Protect(token),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            existing.ProtectedToken = protector.Protect(token);
            existing.UpdatedAtUtc = now;
        }

        db.AuditContext = auditContext;
        db.RaiseDomainEvent(new GitLabTokenSetEvent(actingUserId));

        await db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<GitLabTokenState>.Success(new GitLabTokenState(HasToken: true));
    }

    public async Task<PageMutationResult<GitLabTokenState>> ClearTokenAsync(
        Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var existing = await db.GitLabCredentials
            .FirstOrDefaultAsync(c => c.UserId == actingUserId, cancellationToken);
        if (existing is null)
        {
            // ValidationError rather than NotFoundError: NotFoundError carries a
            // subject id, and the only id here is the caller's own — "you have no
            // token to clear" is a statement about their input, not a missing subject.
            return PageMutationResult<GitLabTokenState>.Failure(
                new ValidationError("No GitLab token is set."));
        }

        db.GitLabCredentials.Remove(existing);

        db.AuditContext = auditContext;
        db.RaiseDomainEvent(new GitLabTokenClearedEvent(actingUserId));

        await db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<GitLabTokenState>.Success(new GitLabTokenState(HasToken: false));
    }

    public Task<bool> HasTokenAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.GitLabCredentials.AsNoTracking().AnyAsync(c => c.UserId == userId, cancellationToken);

    public async Task<string?> GetDecryptedTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var protectedToken = await db.GitLabCredentials.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => c.ProtectedToken)
            .FirstOrDefaultAsync(cancellationToken);

        // Unprotect returning null (lost/rotated key ring, tampered row) degrades to
        // "no credential" — fail closed into the placeholder, never an exception that
        // breaks the page. The user re-enters their token; HasToken still reports true,
        // which is honest: a row exists, it just no longer decrypts here.
        return protectedToken is null ? null : protector.Unprotect(protectedToken);
    }
}
