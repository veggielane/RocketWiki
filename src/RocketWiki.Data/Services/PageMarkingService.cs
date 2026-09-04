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
/// this caller happened to hold canEdit), then canEdit, then input validation (level,
/// caveat, selectors), then the self-lockout constraint. A caller who may not edit this
/// page gets a refusal, never validation feedback about the payload they sent; a caller
/// whose payload is malformed is told so before the gate decides whether they could read
/// the result, because a marking that cannot exist has no readability to decide.</para>
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
        // includes the caller's full marking gate against the page's CURRENT marking: a
        // page you cannot see is a page you cannot re-mark. The constraint further down
        // is the separate question of the marking you are asking for.
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

        var vocabularyResult = ValidateEyesOnly(request.EyesOnly);
        if (vocabularyResult.Error is { } vocabularyError)
        {
            return PageMutationResult<PageMarkingView>.Failure(vocabularyError);
        }

        var selectorResult = ValidateSelectors(_db.SelectorCatalog, request.Selectors);
        if (selectorResult.Error is { } selectorError)
        {
            return PageMutationResult<PageMarkingView>.Failure(selectorError);
        }

        var marking = await _db.PageMarkings
            .Include(m => m.Countries)
            .Include(m => m.Selectors)
            .FirstOrDefaultAsync(m => m.PageId == page.Id, cancellationToken);

        // The before-state for the audit row. A page with no marking row is unavailable
        // everywhere else (ProtectiveMarking.FailClosed: readable by nobody, rendered as
        // a bare TOP SECRET), and the audit row says the same thing rather than inventing
        // a friendlier previous value - the whole point of the before/after pair is that
        // a reviewer can trust it. Unreachable in practice: canEdit above already refused
        // a page whose marking is unavailable, so the only way here is with a row.
        var before = marking?.ToMarking() ?? ProtectiveMarking.FailClosed;

        // The prefix is a toggle (design.md §21.12): UK or nothing. It rides along into the
        // marking and gets no self-lockout constraint, because it is presentational -
        // there is nothing to validate it against and nothing for it to be refused for.
        // The level is presentational too, now that nothing compares a clearance against
        // it, and gets the same treatment: validated as a member of the ladder above,
        // constrained by nothing below. The selectors are the FULL replacement set, like
        // the caveat: a marking is one value, and a partial update would let a caller
        // change the level without ever stating which compartments they meant (§21.15).
        var after = ProtectiveMarking.Create(
            request.Level, vocabularyResult.Countries, selectorResult.Selectors,
            request.UkPrefix ? ProtectiveMarking.UkPrefix : null);

        // design.md §21.6: you may not set a marking you could not then read. Enforced on
        // the resulting marking as a WHOLE through the one composition every read path
        // uses (MarkingGate: selector grant, caveat), because each of them loses you the
        // page just as completely - a UK editor marking a page US EYES ONLY, or asserting
        // a selector this space never granted them, has marked it out of their own reach.
        // What that check no longer means: "a level above your clearance". There is no
        // clearance on this deployment, the level is not in the gate, and so an editor may
        // set any level; the rule now reads "a selector you are not granted here, or a
        // caveat that excludes you", and nothing else. The granted union is the caller's
        // own in THIS space, from the same grants canEdit was just computed from; canEdit
        // implies access, so the null arm below is unreachable and, if it were reached,
        // an empty union would refuse every selector rather than admit one. Deliberately
        // a Forbidden and not a Validation: the input is well-formed, the caller is simply
        // not entitled to the result.
        var access = EffectivePermissionCalculator.ComputeSpaceAccess(context.SpaceGrants, principal);
        var wouldBeReadable = MarkingGate.Check(
            after, principal, _db.SelectorCatalog, access?.GrantedSelectors ?? SpaceAccess.WithoutSelectors.GrantedSelectors);
        if (!wouldBeReadable.IsAllowed)
        {
            return PageMutationResult<PageMarkingView>.Failure(new ForbiddenError(wouldBeReadable.DenialReason!));
        }

        if (marking is null)
        {
            marking = new PageMarking { PageId = page.Id };
            _db.PageMarkings.Add(marking);
        }

        marking.Level = after.Level;
        marking.Prefix = after.Prefix; // already canonical: "UK" or null
        // Always false here - `after` came from ProtectiveMarking.Create, which cannot
        // build the unavailable sentinel - written explicitly so that stating a marking
        // makes it KNOWN: the column carries the value, never a decision made in this
        // service. (Unreachable for a row that was unavailable, since canEdit above
        // already refused a page nobody can read; the write is structural, not a repair
        // path - see IPageMarkingService.)
        marking.IsUnavailable = after.IsUnavailable;
        marking.SetAtUtc = DateTime.UtcNow;
        marking.SetByUserId = actingUserId;
        ReplaceCountries(marking, after.EyesOnly);
        ReplaceSelectors(marking, after.Selectors);

        // The event carries the whole before/after pair, so IsDowngrade sees a removed or
        // swapped selector exactly as it sees a lowered level or a cleared caveat (§21.6),
        // and the audit row records both selector sets in full (§21.7).
        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageMarkingSetEvent(page.Id, space.Id, space.Key, actingUserId, before, after));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageMarkingView>.Success(PageMarkingView.From(after, _db.SelectorCatalog));
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
    /// Replaces the selector set, keyed by category (design.md §21.15): a category no
    /// longer wanted loses its row, a category whose value changed has its row updated in
    /// place, a new category gets a row. Keyed by category rather than by (category,
    /// value) because the PK is <c>(PageId, Category)</c> - one value per category is a
    /// database fact - and a swap expressed as delete-plus-insert of the same key in one
    /// unit of work is exactly the ordering hazard the country diff above avoids.
    /// </summary>
    private void ReplaceSelectors(PageMarking marking, IReadOnlyList<SelectorValue> wanted)
    {
        var wantedByCategory = wanted.ToDictionary(s => s.Category, s => s.Value, StringComparer.Ordinal);

        foreach (var stale in marking.Selectors.Where(s => !wantedByCategory.ContainsKey(s.Category)).ToList())
        {
            marking.Selectors.Remove(stale);
            _db.PageMarkingSelectors.Remove(stale);
        }

        foreach (var row in marking.Selectors)
        {
            row.Value = wantedByCategory[row.Category];
        }

        var held = marking.Selectors.Select(s => s.Category).ToHashSet(StringComparer.Ordinal);
        foreach (var (category, value) in wantedByCategory.Where(kv => !held.Contains(kv.Key)))
        {
            marking.Selectors.Add(new PageMarkingSelector { PageId = marking.PageId, Category = category, Value = value });
        }
    }

    /// <summary>
    /// Validates every requested country against the fixed
    /// <see cref="NationalCaveatVocabulary"/> (design.md §21.4). A token outside the
    /// five is refused rather than stored: the principal side drops unknown tokens, so
    /// a caveat naming one would be released to nobody while reading as perfectly
    /// correct — the failure §21.4 exists to make unrepresentable. Nothing is consulted
    /// in the database; the vocabulary is the same constant on both sides.
    /// </summary>
    private static (IReadOnlyList<string> Countries, PageMutationError? Error) ValidateEyesOnly(
        IReadOnlyList<string>? requested)
    {
        var wanted = ProtectiveMarking.Create(ClassificationLevel.Official, requested).EyesOnly;
        var unknown = wanted.Where(c => !NationalCaveatVocabulary.IsKnown(c)).ToList();
        if (unknown.Count > 0)
        {
            // Echoing the values back leaks nothing - the caller supplied them - and it
            // is the only way the message is actionable.
            return ([], new ValidationError(
                $"'{string.Join("', '", unknown)}' is not a national caveat country. An eyes-only caveat may only " +
                $"name {string.Join(", ", NationalCaveatVocabulary.Values)}, because those are the nationality " +
                "tokens a principal's claim is compared against (design.md §21.4)."));
        }

        return (wanted, null);
    }

    /// <summary>
    /// Validates the requested selectors against the configured catalog (design.md
    /// §21.15) with the same rule the grant writers use — a category or value this
    /// instance has not configured is refused with both named — plus the one rule a
    /// marking adds on top of a grant: <b>at most one value per category</b>. A grant may
    /// confer APPLE and BANANA; a page is one or the other. Refused here, as a
    /// <c>ValidationError</c> that names the category, rather than left to
    /// <see cref="ProtectiveMarking.Create"/>'s exception or the primary key.
    /// </summary>
    private static (IReadOnlyList<SelectorValue> Selectors, PageMutationError? Error) ValidateSelectors(
        SelectorCatalog catalog, IReadOnlyList<SelectorValue>? requested)
    {
        var unknown = AccessRuleValidation.ValidateSelectors(catalog, requested, out var canonical);
        if (unknown is not null)
        {
            return ([], unknown);
        }

        var doubled = canonical
            .GroupBy(s => s.Category, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (doubled is not null)
        {
            return ([], new ValidationError(
                $"Category {doubled.Key} may carry at most one value on a page; " +
                $"'{string.Join("', '", doubled.Select(s => s.Value))}' were requested (design.md §21.15)."));
        }

        return (canonical, null);
    }
}
