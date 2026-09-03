using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Access;

/// <summary>
/// The request-side mirror of <c>CK_AccessRules_KindColumnPairing</c> (data-model.md) and
/// of the selector catalog (design.md §21.15), stated once for both writers of grant rows
/// — <c>AccessRuleService</c> and <c>SpaceService.CreateAsync</c> — so a bad request is a
/// <c>ValidationError</c> that names the problem rather than an opaque
/// <c>DbUpdateException</c>, and so the two writers cannot accept different shapes.
/// </summary>
internal static class AccessRuleValidation
{
    /// <summary>
    /// Which columns a kind may populate. A role grant: SpaceId and a Role that is Editor
    /// or SpaceAdmin, nothing else. An access grant: SpaceId and nothing else — its
    /// selectors live in child rows. A page restriction: PageId and Action, nothing else.
    /// Selectors on anything but an access grant are refused rather than ignored: a
    /// caller who attached them meant something the row cannot say.
    /// </summary>
    public static ValidationError? ValidateShape(
        AccessRuleKind kind, Guid? spaceId, Guid? pageId, SpaceRole? role, PageAction? action,
        IReadOnlyList<SelectorValue>? selectors)
    {
        var hasSelectors = selectors is { Count: > 0 };
        switch (kind)
        {
            case AccessRuleKind.RoleGrant:
                if (spaceId is null || pageId is not null || role is null || action is not null)
                {
                    return new ValidationError("A role grant must set SpaceId and Role, and must not set PageId or Action.");
                }

                if (!Enum.IsDefined(role.Value))
                {
                    // The retired Viewer value (1) and anything else outside the enum:
                    // "viewer" is an access grant now, and the check constraint refuses it.
                    return new ValidationError($"'{role.Value}' is not a role; a role grant confers Editor or SpaceAdmin.");
                }

                if (hasSelectors)
                {
                    return new ValidationError("A role grant confers no selectors; put them on an access grant.");
                }

                return null;

            case AccessRuleKind.AccessGrant:
                if (spaceId is null || pageId is not null || role is not null || action is not null)
                {
                    return new ValidationError("An access grant must set SpaceId only — no Role, PageId or Action.");
                }

                return null;

            case AccessRuleKind.PageRestriction:
                if (pageId is null || spaceId is not null || action is null || role is not null)
                {
                    return new ValidationError("A PageRestriction rule must set PageId and Action, and must not set SpaceId or Role.");
                }

                if (hasSelectors)
                {
                    return new ValidationError("A page restriction carries no selectors.");
                }

                return null;

            default:
                return new ValidationError($"Unknown AccessRuleKind: {kind}.");
        }
    }

    /// <summary>
    /// Canonicalizes the requested selector values and checks each against the
    /// configured catalog (design.md §21.15). Duplicates collapse; several values in one
    /// category are allowed (a grant may confer both APPLE and BANANA). A category or
    /// value this instance has not configured is refused with both named: a grant for a
    /// token nobody configured could never admit anyone, and writing it would be a row
    /// that looks like access and confers none.
    /// </summary>
    public static ValidationError? ValidateSelectors(
        SelectorCatalog catalog, IReadOnlyList<SelectorValue>? requested, out IReadOnlyList<SelectorValue> canonical)
    {
        canonical = [];
        if (requested is null || requested.Count == 0)
        {
            return null;
        }

        var values = requested
            .Where(s => s is not null && s.Category.Length > 0 && s.Value.Length > 0)
            .Distinct()
            .OrderBy(s => s, SelectorValue.CanonicalOrder)
            .ToArray();

        var unknown = values.Where(s => !catalog.IsKnown(s)).ToList();
        if (unknown.Count > 0)
        {
            // Echoing the tokens back leaks nothing - the caller supplied them - and the
            // configured vocabulary is what makes the message actionable.
            return new ValidationError(
                $"'{string.Join("', '", unknown)}' is not a configured selector. This instance's selector " +
                $"categories are: {(catalog.Count == 0 ? "(none)" : string.Join(", ", catalog.Categories.Select(c => $"{c.Name} [{string.Join("|", c.Values)}]")))}.");
        }

        canonical = values;
        return null;
    }
}
