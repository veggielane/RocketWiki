using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Forms;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>One field of a form, as the SPA renders it.</summary>
public sealed record FormFieldView(string Name, FormFieldType Type, bool Required, IReadOnlyList<string> Options);

/// <summary>A form declared on a page.</summary>
public sealed record FormDefinitionView(string Collection, IReadOnlyList<FormFieldView> Fields);

/// <summary>
/// A definition the page declares but that could not be read. Returned rather than
/// dropped so the author sees which collection and what was wrong — a form that silently
/// vanishes is the worst version of this failure.
/// </summary>
public sealed record FormDefinitionErrorView(string Collection, string Message);

/// <summary>What a page declares: the forms it defines, and the ones it got wrong.</summary>
public sealed record PageFormsView(
    IReadOnlyList<FormDefinitionView> Definitions,
    IReadOnlyList<FormDefinitionErrorView> Errors);

public partial class Query
{
    /// <summary>
    /// The forms a page declares (docs/ENTRIES-AND-FORMS-PLAN.md).
    ///
    /// <para><b>Parsed server-side, and only server-side.</b> The definition lives in page
    /// content, so the SPA could parse it too — and then rendering and validation would be
    /// two opinions about what a form is, agreeing right up until they did not. Same rule
    /// §22's RQL follows: one grammar, the server's, with the client deliberately not
    /// re-parsing.</para>
    ///
    /// <para>Gated by the page read itself, so a page you cannot view declares no forms
    /// as far as you are concerned — absent, not forbidden (§6.7). Enumerating from the
    /// DEFINITIONS rather than from stored entries is deliberate: a listing derived from
    /// entries would reveal that a collection holds at least one, to a reader who may be
    /// permitted to see none of them.</para>
    /// </summary>
    [AuditAction("page.view")]
    [UseAuditDispatch]
    public async Task<PageFormsView?> PageForms(
        Guid pageId,
        [Service] IPageReadService pageReads,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        if (await pageReads.GetPageAsync(pageId, principal, cancellationToken) is not ReadResult<Core.Entities.Page>.Found page)
        {
            return null;
        }

        var (definitions, errors) = FormDefinitionParser.Parse(page.Value.CurrentContent);
        return new PageFormsView(
            definitions
                .Select(d => new FormDefinitionView(
                    d.Collection,
                    d.Fields.Select(f => new FormFieldView(f.Name, f.Type, f.Required, f.Options)).ToList()))
                .ToList(),
            errors.Select(e => new FormDefinitionErrorView(e.Collection, e.Message)).ToList());
    }
}
