using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Query;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// Everything the compiler needs to turn names into keys, resolved once by
/// <see cref="PageQueryService"/> before compilation.
///
/// <para><b>Both maps are deliberately incomplete, and that is the security property.</b>
/// <see cref="SpaceIdsByKey"/> holds only spaces the caller holds a role in, so a key that
/// names a space they cannot see is simply absent — and compiles to exactly what a key
/// naming no space at all compiles to: a predicate that matches nothing. There is no branch
/// anywhere that can tell the two apart, which is design.md §6.7's requirement made
/// structural rather than remembered.</para>
///
/// <para><see cref="UserIdsBySubject"/> maps an OIDC subject to the local <c>User.Id</c>
/// that authored revision 1. It is a <i>display/foreign-key</i> lookup on the mirror table,
/// used to filter content — never an authorization input. design.md §6.1's rule
/// ("authorization evaluates the token, never the local User mirror") is untouched: the
/// permission decision downstream runs entirely off the request <c>Principal</c>, and
/// nothing on this record reaches it.</para>
/// </summary>
internal sealed record RqlCompilationContext(
    IReadOnlyDictionary<string, Guid> SpaceIdsByKey,
    IReadOnlyDictionary<string, Guid> UserIdsBySubject,
    string CurrentUserSubject);

/// <summary>
/// Compiles a validated, now-resolved <see cref="RqlQuery"/> into an EF predicate over
/// <see cref="Page"/> (design.md §22).
///
/// <para><b>Everything is a parameter.</b> No user text is ever concatenated into SQL, a
/// LIKE pattern, or anything else the database parses: values arrive as captured locals,
/// which EF Core lifts into query parameters, and the one pattern language RQL does reach —
/// LIKE, for <c>title ~</c> — has its wildcards escaped with an explicit ESCAPE character
/// (<see cref="BuildContainsPattern"/>). <see cref="FullTextQueryBuilder"/> is the local
/// precedent for treating a query mini-language as something to construct carefully rather
/// than interpolate into.</para>
///
/// <para><b>It compiles a filter, and only a filter.</b> Nothing here selects, weakens, or
/// even names a permission check. The predicate decides which pages are <i>candidates</i>;
/// <see cref="PageQueryService"/> then runs the ordinary per-page <c>canView</c> post-filter
/// over them, the same one <c>ILabelService.GetPagesByLabelAsync</c> and
/// <see cref="SearchService"/> run, and no RQL expression is an input to it. That is why
/// RQL may have a <c>NOT</c> at all when design.md §6.3 forbids one in access rules: this
/// NOT negates a content condition inside an already-narrowed candidate set and cannot
/// widen it.</para>
///
/// <para>Provider-agnostic by construction (design.md §14): plain LINQ plus
/// <c>EF.Functions.Like</c>, so the SQLite integration tier exercises the same expression
/// tree SQL Server runs.</para>
/// </summary>
internal static class RqlQueryCompiler
{
    /// <summary>The LIKE escape character. Backslash, escaped in the pattern itself so it is
    /// never ambiguous with a literal one the author typed.</summary>
    private const string LikeEscape = "\\";

    public static Expression<Func<Page, bool>> Compile(RqlNode node, RqlCompilationContext context) => node switch
    {
        RqlNode.And and => and.Children
            .Select(child => Compile(child, context))
            .Aggregate(AndAlso),
        RqlNode.Or or => or.Children
            .Select(child => Compile(child, context))
            .Aggregate(OrElse),
        RqlNode.Not not => Negate(Compile(not.Child, context)),
        RqlNode.Predicate predicate => CompilePredicate(predicate, context),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unhandled RQL node."),
    };

    private static Expression<Func<Page, bool>> CompilePredicate(
        RqlNode.Predicate predicate, RqlCompilationContext context) => predicate.Field switch
    {
        RqlField.Label => CompileLabel(predicate),
        RqlField.Space => CompileSpace(predicate, context),
        RqlField.Title => CompileTitle(predicate),
        RqlField.Created or RqlField.Updated => CompileDate(predicate),
        RqlField.Creator => CompileCreator(predicate, context),
        _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate.Field, "Unhandled RQL field."),
    };

    /// <summary>
    /// Label names match <b>exactly</b>, and deliberately do not follow
    /// <see cref="CompileSpace"/> into case-insensitivity.
    ///
    /// <para>A space key is an <i>address</i> — half of <c>/spaces/{key}/{slug}</c>, typed
    /// by hand, pasted from chat, and case-folded for the same reason every URL is. A
    /// label is a <i>name</i>: user-chosen, offered by a picker rather than typed from
    /// memory, and displayed back as written. Folding it would mean either a normalized
    /// column beside the display name (the PagePropertyKey.KeyNormalized shape, and a real
    /// product decision about whether "Design" and "design" are one label) or a LOWER()
    /// comparison that gives up the index. Neither follows from "URLs are
    /// case-insensitive", so neither is done here.</para>
    ///
    /// <para>The BIN2 collation (data-model.md) made this comparison <i>consistent</i>
    /// rather than stricter in any tier that mattered: SQL Server used to fold it while
    /// SQLite did not, so the two tiers were enforcing different rules and the looser one
    /// was the one under test. It is now ordinal everywhere, matching §6.3's doctrine for
    /// every other user-supplied token. The visible consequence, stated rather than
    /// discovered: a space can now hold both "Design" and "design" as separate labels on
    /// SQL Server, as it always could on SQLite.</para>
    /// </summary>
    private static Expression<Func<Page, bool>> CompileLabel(RqlNode.Predicate predicate)
    {
        switch (predicate.Operator)
        {
            case RqlOperator.IsEmpty:
                return p => !p.PageLabels.Any();
            case RqlOperator.IsNotEmpty:
                return p => p.PageLabels.Any();
        }

        var names = TextValues(predicate);
        Expression<Func<Page, bool>> any = p => p.PageLabels.Any(pl => names.Contains(pl.Label!.Name));

        return predicate.Operator is RqlOperator.NotEquals or RqlOperator.NotIn ? Negate(any) : any;
    }

    /// <summary>
    /// Space keys resolve through the caller's <i>visible</i> space map only (see
    /// <see cref="RqlCompilationContext"/>). An unresolvable key contributes no id, so
    /// <c>space = "BLACKPROJECT"</c> becomes "no space id matches" whether that key names a
    /// space the caller cannot see or no space at all — identical predicates, identical
    /// empty results, no branch between them.
    ///
    /// <para>The key is canonicalized before the lookup, so <c>space = "eng"</c> and
    /// <c>space = "ENG"</c> are the same query — a space key means the same thing in RQL
    /// as it does in a URL. The map's own keys come from <c>Space.Key</c>, which is stored
    /// canonically, so this is a comparison between two canonical forms and stays the
    /// in-memory ordinal lookup it always was. Note this deliberately does NOT extend to
    /// <c>label</c>: see <see cref="CompileLabel"/>.</para>
    /// </summary>
    private static Expression<Func<Page, bool>> CompileSpace(
        RqlNode.Predicate predicate, RqlCompilationContext context)
    {
        var spaceIds = TextValues(predicate)
            .Select(key => context.SpaceIdsByKey.TryGetValue(SpaceKeys.Canonical(key), out var id) ? (Guid?)id : null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        Expression<Func<Page, bool>> matches;
        if (spaceIds.Count == 0)
        {
            matches = _ => false;
        }
        else
        {
            matches = p => spaceIds.Contains(p.SpaceId);
        }

        return predicate.Operator is RqlOperator.NotEquals or RqlOperator.NotIn ? Negate(matches) : matches;
    }

    private static Expression<Func<Page, bool>> CompileTitle(RqlNode.Predicate predicate)
    {
        var value = TextValues(predicate)[0];

        switch (predicate.Operator)
        {
            case RqlOperator.Equals:
                return p => p.Title == value;
            case RqlOperator.NotEquals:
                return p => p.Title != value;
        }

        var pattern = BuildContainsPattern(value);
        Expression<Func<Page, bool>> contains = p => EF.Functions.Like(p.Title, pattern, LikeEscape);
        return predicate.Operator == RqlOperator.NotContains ? Negate(contains) : contains;
    }

    /// <summary>
    /// Wraps the author's text in <c>%…%</c> after escaping every character LIKE would
    /// otherwise read as a wildcard or a character class — <c>%</c>, <c>_</c>, <c>[</c>, and
    /// the escape character itself. Without this, <c>title ~ "50%"</c> silently becomes a
    /// prefix search and <c>title ~ "[draft]"</c> becomes a character class matching one of
    /// five letters, which is wrong long before it is dangerous.
    /// </summary>
    internal static string BuildContainsPattern(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('%');
        foreach (var c in value)
        {
            if (c is '%' or '_' or '[' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.Append('%').ToString();
    }

    /// <summary>
    /// Date comparisons, built over the column the field names.
    ///
    /// <para>The subtlety is <b>day precision</b>. A date written without a time
    /// (<c>"2026-01-31"</c>) denotes the whole UTC day, so <c>=</c> becomes a half-open range
    /// test and <c>&gt;</c> means "after that day ends", not "after that day began". An
    /// instant — including every resolved <c>now()</c> — compares exactly. Getting this wrong
    /// is not a subtle bug: <c>created = "2026-01-31"</c> compiled as exact equality would
    /// match only a page created at precisely midnight, i.e. essentially never, while looking
    /// like it worked.</para>
    /// </summary>
    private static Expression<Func<Page, bool>> CompileDate(RqlNode.Predicate predicate)
    {
        var date = (RqlValue.Date)predicate.Values[0];
        var parameter = Expression.Parameter(typeof(Page), "p");
        var column = Expression.Property(
            parameter,
            predicate.Field == RqlField.Created ? nameof(Page.CreatedAtUtc) : nameof(Page.UpdatedAtUtc));

        var start = Parameterized(date.StartUtc);
        var end = date.EndExclusiveUtc is { } endValue ? Parameterized(endValue) : null;

        Expression body = predicate.Operator switch
        {
            RqlOperator.Equals => end is null
                ? Expression.Equal(column, start)
                : Expression.AndAlso(
                    Expression.GreaterThanOrEqual(column, start), Expression.LessThan(column, end)),
            RqlOperator.NotEquals => end is null
                ? Expression.NotEqual(column, start)
                : Expression.OrElse(
                    Expression.LessThan(column, start), Expression.GreaterThanOrEqual(column, end)),
            RqlOperator.GreaterThan => end is null
                ? Expression.GreaterThan(column, start)
                : Expression.GreaterThanOrEqual(column, end),
            RqlOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(column, start),
            RqlOperator.LessThan => Expression.LessThan(column, start),
            RqlOperator.LessThanOrEqual => end is null
                ? Expression.LessThanOrEqual(column, start)
                : Expression.LessThan(column, end),
            _ => throw new ArgumentOutOfRangeException(
                nameof(predicate), predicate.Operator, "Unhandled RQL date operator."),
        };

        return Expression.Lambda<Func<Page, bool>>(body, parameter);
    }

    /// <summary>
    /// A page's creator is the author of its <b>first revision</b> — there is no
    /// <c>CreatedByUserId</c> column on <c>Pages</c> (data-model.md), and adding one for a
    /// query filter would be a schema change in service of a convenience. A page with no
    /// revision 1 (nothing the API creates, but a bundle import could in principle produce
    /// one) therefore matches no creator, which is the honest answer for a page whose author
    /// is not recorded.
    ///
    /// <para>An unresolvable subject yields the same never-matches predicate an
    /// unresolvable space key does: an unknown user is indistinguishable from one who has
    /// created nothing the caller may see.</para>
    /// </summary>
    private static Expression<Func<Page, bool>> CompileCreator(
        RqlNode.Predicate predicate, RqlCompilationContext context)
    {
        var subject = predicate.Values[0] switch
        {
            RqlValue.CurrentUser => context.CurrentUserSubject,
            RqlValue.Text text => text.Value,
            var other => throw new ArgumentOutOfRangeException(
                nameof(predicate), other, "Unhandled RQL creator value."),
        };

        Expression<Func<Page, bool>> matches;
        if (context.UserIdsBySubject.TryGetValue(subject, out var userId))
        {
            matches = p => p.Revisions.Any(r => r.RevisionNumber == 1 && r.AuthorUserId == userId);
        }
        else
        {
            matches = _ => false;
        }

        return predicate.Operator == RqlOperator.NotEquals ? Negate(matches) : matches;
    }

    private static IReadOnlyList<string> TextValues(RqlNode.Predicate predicate) =>
        predicate.Values.Cast<RqlValue.Text>().Select(v => v.Value).ToList();

    /// <summary>
    /// Lifts a value into the shape the C# compiler emits for a captured local — a property
    /// read off a closure object — so EF Core's parameter extraction turns it into a real
    /// query parameter rather than inlining it as a SQL literal. Only the hand-built date
    /// comparisons need this; every other leaf is written as an ordinary lambda over
    /// captured locals and is parameterized for free.
    /// </summary>
    private static Expression Parameterized<T>(T value) =>
        Expression.Property(Expression.Constant(new ValueBox<T>(value)), nameof(ValueBox<T>.Value));

    private sealed class ValueBox<T>(T value)
    {
        public T Value { get; } = value;
    }

    private static Expression<Func<Page, bool>> AndAlso(
        Expression<Func<Page, bool>> left, Expression<Func<Page, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    private static Expression<Func<Page, bool>> OrElse(
        Expression<Func<Page, bool>> left, Expression<Func<Page, bool>> right) =>
        Combine(left, right, Expression.OrElse);

    private static Expression<Func<Page, bool>> Negate(Expression<Func<Page, bool>> inner) =>
        Expression.Lambda<Func<Page, bool>>(Expression.Not(inner.Body), inner.Parameters[0]);

    /// <summary>
    /// Joins two predicates under one parameter. <c>Expression.Invoke</c> would be shorter
    /// and EF Core cannot translate it, so the two bodies are rewritten onto a shared
    /// parameter instead.
    /// </summary>
    private static Expression<Func<Page, bool>> Combine(
        Expression<Func<Page, bool>> left,
        Expression<Func<Page, bool>> right,
        Func<Expression, Expression, BinaryExpression> join)
    {
        var parameter = Expression.Parameter(typeof(Page), "p");
        var body = join(
            ParameterRewriter.Rewrite(left.Body, left.Parameters[0], parameter),
            ParameterRewriter.Rewrite(right.Body, right.Parameters[0], parameter));
        return Expression.Lambda<Func<Page, bool>>(body, parameter);
    }

    private sealed class ParameterRewriter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        public static Expression Rewrite(Expression body, ParameterExpression from, ParameterExpression to) =>
            new ParameterRewriter(from, to).Visit(body);

        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
