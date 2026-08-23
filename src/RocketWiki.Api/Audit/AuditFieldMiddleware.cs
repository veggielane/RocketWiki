using System.Reflection;
using HotChocolate.Resolvers;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.Audit;

/// <summary>
/// design.md §8's "emission half" of the audit declaration: reads the
/// <see cref="AuditActionAttribute"/> off the field's resolver method and, on a
/// non-null result, calls <see cref="IAuditSink"/>. Attached per-field via
/// <see cref="UseAuditDispatchAttribute"/> — deliberately NOT a blanket
/// <c>UseField</c> across the whole schema, and never attached to Mutation fields:
/// mutations already get their success audit row from the domain-event pipeline in
/// the same transaction (RocketWikiDbContext.RaiseDomainEvent), and dispatching here
/// too would double-audit. Denied mutations are audited explicitly in the mutation
/// resolvers themselves, where the typed error (with its reason) is available —
/// this middleware only ever sees Outcome.Success.
///
/// A null result is never audited here: by the time this middleware runs, the resolver
/// has already collapsed the internal ReadResult (design.md §6.7) to null, and the two
/// things null can mean diverge in what they deserve — a denial was *already audited by
/// the resolver itself* (with the failing-restriction reason this middleware could never
/// see; ReadDenialAudit is that path), while a genuine not-found is deliberately not
/// audited at all (no access decision was made; see ReadDenialAudit's doc). So "skip
/// null" is not a gap anymore, it is the success-only half of the split. For non-null
/// results that a resolver collapsed a denial *into* (an empty tree), DbAuditSink
/// suppresses this middleware's Success row for a subject already recorded as Denied
/// this request.
/// </summary>
public sealed class AuditFieldMiddleware(FieldDelegate next)
{
    public async Task InvokeAsync(IMiddlewareContext context)
    {
        await next(context);

        if (context.Result is null)
        {
            return;
        }

        var member = context.Selection.Field.ResolverMember ?? context.Selection.Field.Member;
        var action = member?.GetCustomAttribute<AuditActionAttribute>()?.Action;
        if (action is null)
        {
            return;
        }

        var (subjectType, subjectId, spaceKey) = DescribeSubject(context, action);
        var sink = context.Service<IAuditSink>();
        await sink.RecordAsync(new AuditRecord(action, AuditOutcome.Success, subjectType, subjectId, spaceKey), context.RequestAborted);
    }

    private static (AuditSubjectType? SubjectType, Guid? SubjectId, string? SpaceKey) DescribeSubject(
        IMiddlewareContext context, string action)
    {
        // A field that returns a Page directly (Query.page, Page.parent) vs. one whose
        // *parent* object is the Page being read (Page.content, Page.revisions) - either
        // way, the Page being viewed is the subject, never the field's own return value
        // when that value isn't itself a Page (a content string, a revision list).
        if (context.Result is Page resultPage)
        {
            return (AuditSubjectType.Page, resultPage.Id, null);
        }

        if (context.Parent<object?>() is Page parentPage)
        {
            return (AuditSubjectType.Page, parentPage.Id, null);
        }

        // Query.Space(key) - Query.Spaces (a list) has no single subject and falls
        // through to the argument-based space.browse case below (which also finds
        // nothing on that field, since Spaces has no arguments either), and then to
        // the final null case, same as any other listing query.
        if (context.Result is Space resultSpace)
        {
            return (AuditSubjectType.Space, resultSpace.Id, resultSpace.Key);
        }

        // Query.PageTree(spaceId) - the only space.browse field whose result is neither
        // a Page nor a Space (a list of PageTreeNode), so its own spaceId argument is
        // the subject. Guarded on the field actually declaring that argument: Query.Space
        // and Query.Spaces also emit "space.browse" but take no spaceId argument at all
        // (Space's own case above already handles the former; ArgumentOptional would
        // throw on either if called unconditionally for every space.browse field).
        if (action == "space.browse" && context.Selection.Field.Arguments.ContainsField("spaceId"))
        {
            var spaceId = context.ArgumentOptional<Guid?>("spaceId");
            return (AuditSubjectType.Space, spaceId.HasValue ? spaceId.Value : null, null);
        }

        return (null, null, null);
    }
}
