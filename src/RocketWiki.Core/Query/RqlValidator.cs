using System.Globalization;

namespace RocketWiki.Core.Query;

/// <summary>
/// Turns the parser's untyped tree into the typed <see cref="RqlQuery"/>, refusing
/// everything design.md §22 says RQL will not answer: fields outside the closed set,
/// operators a field does not accept, functions in the wrong place or with the wrong
/// arity, and unparseable dates.
///
/// <para><b>It never touches the database, and that is a security property, not an
/// implementation detail.</b> Validation is about <i>syntax and vocabulary only</i>. It
/// does not know, and must never learn, whether a space key names a real space, whether a
/// label exists, or whether a user id is anybody. design.md §6.7 requires an invisible
/// space to be indistinguishable from a nonexistent one, and the shortest route to
/// breaking that is a well-meaning "no such space 'BLACKPROJECT'" error. Existence is
/// decided by executing the query against permission-filtered candidates and finding
/// nothing — which is also, byte for byte, what a genuinely nonexistent key produces.
/// This is also why <c>parseRql</c> can be answered for any authenticated caller: there
/// is nothing here for it to leak.</para>
///
/// <para>Unlike the parser, this collects <b>every</b> mistake it finds rather than
/// stopping at the first — the shape is already known to be sound, so a second vocabulary
/// error is a real second thing to fix, and the SPA underlines them all at once.</para>
/// </summary>
internal static class RqlValidator
{
    private const string CurrentUserFunction = "currentUser";
    private const string NowFunction = "now";

    public static (RqlQuery? Query, IReadOnlyList<RqlError> Errors) Validate(RawQuery raw)
    {
        var errors = new List<RqlError>();
        var where = ValidateNode(raw.Where, errors);
        var orderBy = ValidateOrderBy(raw.OrderBy, errors);

        return errors.Count > 0 || where is null ? (null, errors) : (new RqlQuery(where, orderBy), errors);
    }

    private static RqlNode? ValidateNode(RawNode node, List<RqlError> errors)
    {
        switch (node)
        {
            case RawGroup group:
            {
                var children = new List<RqlNode>(group.Children.Count);
                foreach (var child in group.Children)
                {
                    var validated = ValidateNode(child, errors);
                    if (validated is not null)
                    {
                        children.Add(validated);
                    }
                }

                if (children.Count == 0)
                {
                    return null;
                }

                // A single surviving child can only happen when siblings failed validation,
                // in which case `errors` is non-empty and nothing will be built from this.
                return children.Count == 1
                    ? children[0]
                    : group.IsOr ? new RqlNode.Or(children) : new RqlNode.And(children);
            }

            case RawNot not:
            {
                var child = ValidateNode(not.Child, errors);
                return child is null ? null : new RqlNode.Not(child);
            }

            case RawPredicate predicate:
                return ValidatePredicate(predicate, errors);

            default:
                throw new ArgumentOutOfRangeException(nameof(node), node, "Unhandled RQL parse node.");
        }
    }

    private static RqlNode? ValidatePredicate(RawPredicate predicate, List<RqlError> errors)
    {
        if (!TryResolveField(predicate.FieldName, predicate.FieldSpan, errors, out var field))
        {
            return null;
        }

        if (!RqlVocabulary.Accepts(field, predicate.Operator))
        {
            errors.Add(new RqlError(
                RqlErrorCode.OperatorNotAllowed,
                RqlVocabulary.DescribeOperatorNotAllowed(field, predicate.Operator),
                predicate.OperatorSpan));
            return null;
        }

        var values = new List<RqlValue>(predicate.Values.Count);
        var ok = true;
        foreach (var rawValue in predicate.Values)
        {
            var value = ValidateValue(field, rawValue, errors);
            if (value is null)
            {
                ok = false;
                continue;
            }

            values.Add(value);
        }

        return ok ? new RqlNode.Predicate(field, predicate.Operator, values, predicate.Span) : null;
    }

    /// <summary>
    /// The three refusals, in the order that keeps each message honest: a
    /// classification/permission name is <b>not queryable</b> (and says why), a real but
    /// unshipped field is <b>not supported yet</b> (and says why), and anything else is
    /// <b>unknown</b>. Checking not-queryable first matters — several of those names would
    /// otherwise read as ordinary typos.
    /// </summary>
    private static bool TryResolveField(
        string name, RqlSpan span, List<RqlError> errors, out RqlField field)
    {
        if (RqlVocabulary.TryResolveField(name, out field))
        {
            return true;
        }

        if (RqlVocabulary.IsNotQueryable(name))
        {
            errors.Add(new RqlError(
                RqlErrorCode.NotQueryableField, RqlVocabulary.DescribeNotQueryable(name), span));
            return false;
        }

        errors.Add(RqlVocabulary.IsNotYetSupported(name)
            ? new RqlError(RqlErrorCode.UnsupportedField, RqlVocabulary.DescribeUnsupported(name), span)
            : new RqlError(RqlErrorCode.UnknownField, RqlVocabulary.DescribeUnknown(name), span));
        return false;
    }

    private static RqlValue? ValidateValue(RqlField field, RawValue rawValue, List<RqlError> errors)
    {
        return rawValue switch
        {
            RawFunction function => ValidateFunction(field, function, errors),
            RawLiteral literal => ValidateLiteral(field, literal, errors),
            _ => throw new ArgumentOutOfRangeException(nameof(rawValue), rawValue, "Unhandled RQL value."),
        };
    }

    private static RqlValue? ValidateLiteral(RqlField field, RawLiteral literal, List<RqlError> errors)
    {
        if (!RqlVocabulary.IsDateField(field))
        {
            return new RqlValue.Text(literal.Text) { Span = literal.Span };
        }

        if (RqlDateLiteral.TryParse(literal.Text, out var start, out var end, out var canonical))
        {
            return new RqlValue.Date(start, end, canonical) { Span = literal.Span };
        }

        errors.Add(new RqlError(
            RqlErrorCode.InvalidValue,
            string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' is not a valid date for field '{1}'. Use an ISO-8601 date (\"2026-01-31\"), an "
                + "ISO-8601 instant (\"2026-01-31T09:15:00Z\"), or now() with an optional offset such as "
                + "now(\"-7d\").",
                literal.Text,
                RqlVocabulary.NameOf(field)),
            literal.Span));
        return null;
    }

    private static RqlValue? ValidateFunction(RqlField field, RawFunction function, List<RqlError> errors)
    {
        if (string.Equals(function.Name, CurrentUserFunction, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateCurrentUser(field, function, errors);
        }

        if (string.Equals(function.Name, NowFunction, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateNow(field, function, errors);
        }

        errors.Add(new RqlError(
            RqlErrorCode.UnknownFunction,
            string.Format(
                CultureInfo.InvariantCulture,
                "Unknown function '{0}()'. Supported functions are currentUser() and now().",
                function.Name),
            function.Span));
        return null;
    }

    private static RqlValue? ValidateCurrentUser(RqlField field, RawFunction function, List<RqlError> errors)
    {
        if (field != RqlField.Creator)
        {
            errors.Add(new RqlError(
                RqlErrorCode.FunctionNotAllowedHere,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "currentUser() is only valid for field 'creator', not '{0}'.",
                    RqlVocabulary.NameOf(field)),
                function.Span));
            return null;
        }

        if (function.Argument is not null)
        {
            errors.Add(new RqlError(
                RqlErrorCode.InvalidValue,
                "currentUser() takes no arguments.",
                function.ArgumentSpan));
            return null;
        }

        return new RqlValue.CurrentUser { Span = function.Span };
    }

    private static RqlValue? ValidateNow(RqlField field, RawFunction function, List<RqlError> errors)
    {
        if (!RqlVocabulary.IsDateField(field))
        {
            errors.Add(new RqlError(
                RqlErrorCode.FunctionNotAllowedHere,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "now() is only valid for the date fields 'created' and 'updated', not '{0}'.",
                    RqlVocabulary.NameOf(field)),
                function.Span));
            return null;
        }

        if (function.Argument is null)
        {
            return new RqlValue.Now(null) { Span = function.Span };
        }

        if (RqlDateLiteral.TryParseOffset(function.Argument, out _, out var canonicalOffset))
        {
            return new RqlValue.Now(canonicalOffset) { Span = function.Span };
        }

        errors.Add(new RqlError(
            RqlErrorCode.InvalidValue,
            string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' is not a valid now() offset. Use a signed amount and a unit of w (weeks), d (days), "
                + "h (hours) or m (minutes) — for example now(\"-7d\") or now(\"+1h\").",
                function.Argument),
            function.ArgumentSpan));
        return null;
    }

    private static IReadOnlyList<RqlOrderItem> ValidateOrderBy(
        IReadOnlyList<RawOrderItem> rawItems, List<RqlError> errors)
    {
        var items = new List<RqlOrderItem>(rawItems.Count);

        foreach (var raw in rawItems)
        {
            if (!TryResolveField(raw.FieldName, raw.FieldSpan, errors, out var field))
            {
                continue;
            }

            if (!RqlVocabulary.IsSortable(field))
            {
                errors.Add(new RqlError(
                    RqlErrorCode.OperatorNotAllowed,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Field '{0}' cannot be used in ORDER BY. Sortable fields are: {1}.",
                        RqlVocabulary.NameOf(field),
                        RqlVocabulary.SortableFieldList),
                    raw.FieldSpan));
                continue;
            }

            items.Add(new RqlOrderItem(field, raw.Direction, raw.Span));
        }

        return items;
    }
}
