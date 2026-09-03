using System.Reflection;
using HotChocolate.Resolvers;
using RocketWiki.Api.GraphQL;
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
/// An unauthenticated request is never audited here either — see the guard in
/// <see cref="InvokeAsync"/> for why that distinction has to be drawn at this layer
/// rather than inside <see cref="DbAuditSink"/>.
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
///
/// <para>A <see cref="PageAccessResult"/> carrying a placeholder is the third shape of
/// "the read was refused" (design.md §6.7 / §21.8): non-null, because the placeholder
/// reaches the caller, but not a success — the resolver recorded the Denied row, and
/// recording a Success beside it would claim a read that never happened. It is skipped
/// here outright rather than left to the sink's suppression, so the rule does not depend
/// on the two rows sharing a subject id.</para>
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

        // A placeholder is a refused read that the caller is shown (§6.7/§21.8): the
        // denial was audited in the resolver with its reason, and there is no success
        // to record.
        if (context.Result is PageAccessResult { Page: null })
        {
            return;
        }

        var member = context.Selection.Field.ResolverMember ?? context.Selection.Field.Member;
        var action = member?.GetCustomAttribute<AuditActionAttribute>()?.Action;
        if (action is null)
        {
            return;
        }

        // An anonymous caller made no access decision worth recording: every read root
        // answers an unauthenticated request with the same absent shape it gives anyone
        // (Query.Spaces' empty list, Query.Search's empty connection), which is as much
        // a non-decision as the null result skipped above — Query.Page's doc already
        // states that an anonymous read writes no row. Those shapes are non-null though,
        // so without this the dispatch below reached DbAuditSink, which throws on a
        // request with no acting user and turned every such query into an execution
        // error. The guard belongs here rather than in the sink because the sink's throw
        // must keep firing for an *authenticated* request that resolved no acting user:
        // that one is a genuine bug, and only the authentication state separates the two.
        // Skipped solely on positive proof of anonymity — with no HttpContext to ask, the
        // sink still decides, so a missing audit row is never the quiet consequence of
        // this middleware not knowing who the caller was.
        var httpContext = context.Service<IHttpContextAccessor>().HttpContext;
        if (httpContext is not null && httpContext.User.Identity?.IsAuthenticated != true)
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

        // Query.pageAccess / pageAccessBySlug with a viewable page: the page is the
        // subject exactly as it would be on Query.page. (The placeholder case never
        // reaches here - see InvokeAsync.)
        if (context.Result is PageAccessResult { Page: { } accessPage })
        {
            return (AuditSubjectType.Page, accessPage.Id, null);
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
        // a Page nor a Space (a list of tree entries), so its own spaceId argument is
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
