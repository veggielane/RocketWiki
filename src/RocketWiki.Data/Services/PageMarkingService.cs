using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed <see cref="IPageMarkingService"/> (design.md §21). Lives in RocketWiki.Data
/// for the same reason every other service here does: it needs RocketWikiDbContext
/// directly.
///
/// <para>Gate order matches every other page mutation and is not arbitrary: replica
/// first (it refuses beneath every grant, §12, so the answer must not depend on whether
/// this caller happened to hold canEdit), then canEdit, then the clearance constraint,
/// then input validation. A caller who may not edit this page gets a refusal, never
/// validation feedback about the payload they sent.</para>
/// </summary>
public class PageMarkingService : IPageMarkingService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;
    private readonly PermissionContextLoader _permissions;

    public PageMarkingService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
        _permissions = new PermissionContextLoader(db);
    }

    public async Task<PageMutationResult<PageMarkingView>> SetAsync(
        SetPageMarkingRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<PageMarkingView>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<PageMarkingView>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<PageMarkingView>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        // canEdit is computed through the one loader (design.md §6.7/§21), so it already
        // includes the caller's clearance against the page's CURRENT marking: a page you
        // cannot see is a page you cannot re-mark. The constraint below is the separate
        // question of the marking you are asking for.
        var context = await _permissions.LoadAsync(page, isReplicaSpace: false, cancellationToken);
        if (!context.Compute(principal).CanEdit)
        {
            return PageMutationResult<PageMarkingView>.Failure(new ForbiddenError("canEdit required"));
        }

        if (!Enum.IsDefined(request.Level))
        {
            // Reachable from a non-GraphQL caller (MCP, a future REST route); the enum
            // binder would have rejected it earlier on the GraphQL path. A level outside
            // the ladder has no defined ordering, so there is nothing safe to do with it.
            return PageMutationResult<PageMarkingView>.Failure(new ValidationError(
                $"'{request.Level}' is not a classification level."));
        }

        var vocabularyResult = await ValidateEyesOnlyAsync(request.EyesOnly, cancellationToken);
        if (vocabularyResult.Error is { } vocabularyError)
        {
            return PageMutationResult<PageMarkingView>.Failure(vocabularyError);
        }

        var after = ProtectiveMarking.Create(request.Level, vocabularyResult.Countries);

        // design.md §21: you may not set a marking you could not then read. Enforced as
        // the resulting marking as a WHOLE, not just its level, because the caveat loses
        // you the page just as completely - a GB editor marking a page [US EYES ONLY] has
        // classified it out of their own reach exactly as surely as over-classifying it.
        // Deliberately a Forbidden and not a Validation: the input is well-formed, the
        // caller is simply not entitled to the result.
        var wouldBeReadable = ClearanceGate.Check(after, principal);
        if (!wouldBeReadable.IsAllowed)
        {
            return PageMutationResult<PageMarkingView>.Failure(new ForbiddenError(wouldBeReadable.DenialReason!));
        }

        var marking = await _db.PageMarkings
            .Include(m => m.Countries)
            .FirstOrDefaultAsync(m => m.PageId == page.Id, cancellationToken);

        // The before-state for the audit row. A page with no marking row reads as
        // TOP SECRET everywhere else (ProtectiveMarking.FailClosed), and the audit row
        // says the same thing rather than inventing a friendlier previous value - the
        // whole point of the before/after pair is that a reviewer can trust it.
        var before = marking?.ToMarking() ?? ProtectiveMarking.FailClosed;

        if (marking is null)
        {
            marking = new PageMarking { PageId = page.Id };
            _db.PageMarkings.Add(marking);
        }

        marking.Level = after.Level;
        marking.SetAtUtc = DateTime.UtcNow;
        marking.SetByUserId = actingUserId;
        ReplaceCountries(marking, after.EyesOnly);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageMarkingSetEvent(page.Id, space.Id, space.Key, actingUserId, before, after));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageMarkingView>.Success(PageMarkingView.From(after));
    }

    /// <summary>
    /// Replaces the country set by removing what is no longer wanted and adding what is
    /// new, rather than clearing and re-adding everything: with a composite PK of
    /// (PageId, CountryValue), a delete-then-insert of an unchanged row is a pointless
    /// pair of statements in the transaction and, on some providers, an ordering hazard.
    /// </summary>
    private void ReplaceCountries(PageMarking marking, IReadOnlyList<string> wanted)
    {
        var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);

        foreach (var existing in marking.Countries.Where(c => !wantedSet.Contains(c.CountryValue)).ToList())
        {
            marking.Countries.Remove(existing);
            _db.PageMarkingCountries.Remove(existing);
        }

        var held = marking.Countries.Select(c => c.CountryValue).ToHashSet(StringComparer.Ordinal);
        foreach (var country in wanted.Where(c => !held.Contains(c)))
        {
            marking.Countries.Add(new PageMarkingCountry { PageId = marking.PageId, CountryValue = country });
        }
    }

    /// <summary>
    /// Validates every requested country against the registered <c>nationality</c>
    /// attribute's allowed values — see <see cref="NationalityVocabulary"/> for why the
    /// vocabulary must be that registry and not an ISO list, and why getting this wrong
    /// produces a control that denies everyone while looking correct.
    /// </summary>
    private async Task<(IReadOnlyList<string> Countries, PageMutationError? Error)> ValidateEyesOnlyAsync(
        IReadOnlyList<string>? requested, CancellationToken cancellationToken)
    {
        var wanted = ProtectiveMarking.Create(ClassificationLevel.Official, requested).EyesOnly;
        if (wanted.Count == 0)
        {
            // No caveat asked for: the registry is irrelevant, and requiring one here
            // would make markings unusable on an instance that has no nationality
            // attribute at all - which is a perfectly reasonable instance to be.
            return ([], null);
        }

        var definition = await _db.AttributeDefinitions
            .FirstOrDefaultAsync(a => a.Key == ClearanceGate.NationalityAttributeKey, cancellationToken);
        var allowed = NationalityVocabulary.Parse(definition?.AllowedValuesJson);
        if (allowed.Count == 0)
        {
            return ([], new ValidationError(
                $"No '{ClearanceGate.NationalityAttributeKey}' attribute with allowed values is registered, so an " +
                "eyes-only caveat cannot be set: there is no country vocabulary to draw from, and a caveat naming " +
                "values the instance does not recognise would match nobody."));
        }

        var unknown = wanted.Where(c => !allowed.Contains(c)).ToList();
        if (unknown.Count > 0)
        {
            // Echoing the values back leaks nothing - the caller supplied them - and it
            // is the only way the message is actionable.
            return ([], new ValidationError(
                $"'{string.Join("', '", unknown)}' is not an allowed value of the " +
                $"'{ClearanceGate.NationalityAttributeKey}' attribute. An eyes-only caveat may only name countries " +
                "from that attribute's registered values, because that is what a principal's nationality claim is " +
                "compared against."));
        }

        return (wanted, null);
    }
}
