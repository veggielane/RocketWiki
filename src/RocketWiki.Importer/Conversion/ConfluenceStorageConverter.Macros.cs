using System.Xml.Linq;
using RocketWiki.Importer.Conversion.Internal;

namespace RocketWiki.Importer.Conversion;

public sealed partial class ConfluenceStorageConverter
{
    private static string? RenderAcBlockElement(XElement element, RenderState state)
    {
        switch (element.Name.LocalName)
        {
            case "structured-macro":
                return RenderStructuredMacro(element, state);
            case "task-list":
                return RenderTaskList(element, state);
            case "layout":
                return RenderLayout(element, state);
            case "link":
            case "image":
                // ac:link / ac:image used directly at block level, without a wrapping <p> —
                // route through the inline renderer so the same resolution logic applies.
                return RenderInlineTrimmed([element], state);
            default:
                return RenderUnrecognizedAc(element, state);
        }
    }

    private static string RenderAcInlineElement(XElement element, RenderState state)
    {
        switch (element.Name.LocalName)
        {
            case "link":
                return RenderAcLink(element, state);
            case "image":
                return RenderAcImage(element, state);
            case "structured-macro":
                return RenderStructuredMacro(element, state) ?? string.Empty;
            case "placeholder":
                return MarkdownText.Escape(MarkdownText.NormalizeWhitespace(element.Value));
            default:
                return RenderUnrecognizedAc(element, state) ?? string.Empty;
        }
    }

    private static string? RenderUnrecognizedAc(XElement element, RenderState state)
    {
        var text = MarkdownText.NormalizeWhitespace(element.Value);
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.DroppedElement,
            text.Length > 0
                ? $"Unrecognized Confluence element <ac:{element.Name.LocalName}> was unwrapped; its text content was kept but any special behavior was lost."
                : $"Unrecognized Confluence element <ac:{element.Name.LocalName}> was dropped; it had no extractable text content.",
            state.CurrentLocation,
            Snippet(element)));
        return text.Length == 0 ? null : MarkdownText.Escape(text);
    }

    private static string? RenderLayout(XElement element, RenderState state)
    {
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.LossyTransform,
            "Confluence multi-column page layout has no Markdown equivalent; the columns were flattened into sequential content in source order.",
            state.CurrentLocation));

        var cellBlocks = new List<string>();
        foreach (var cell in element.Descendants(ConfluenceNamespaces.Ac + "layout-cell"))
        {
            cellBlocks.AddRange(RenderBlocks(cell.Nodes(), state));
        }

        return cellBlocks.Count == 0 ? null : JoinBlocks(cellBlocks);
    }

    private static string? RenderTaskList(XElement element, RenderState state)
    {
        var tasks = element.Elements(ConfluenceNamespaces.Ac + "task").ToList();
        if (tasks.Count == 0)
        {
            return null;
        }

        var lines = new List<string>(tasks.Count);
        foreach (var task in tasks)
        {
            var status = task.Element(ConfluenceNamespaces.Ac + "task-status")?.Value.Trim();
            var body = task.Element(ConfluenceNamespaces.Ac + "task-body");
            var text = body is null ? string.Empty : RenderInlineTrimmed(body.Nodes(), state);
            var check = string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase) ? "x" : " ";
            lines.Add($"- [{check}] {text}");
        }

        return string.Join('\n', lines);
    }

    private static string? RenderStructuredMacro(XElement macro, RenderState state)
    {
        var macroName = (string?)macro.Attribute(ConfluenceNamespaces.Ac + "name") ?? "unknown";
        return macroName switch
        {
            "code" => RenderCodeMacro(macro),
            "info" => RenderCalloutMacro(macro, state, "info", macroName),
            "note" => RenderCalloutMacro(macro, state, "note", macroName),
            "warning" => RenderCalloutMacro(macro, state, "warning", macroName),
            "tip" => RenderCalloutMacro(macro, state, "info", macroName),
            "toc" => RenderTocMacro(state),
            "status" => RenderStatusMacro(macro, state),
            "expand" => RenderExpandMacro(macro, state),
            _ => RenderUnsupportedMacro(macro, state, macroName),
        };
    }

    private static string? GetMacroParameter(XElement macro, string name) =>
        macro.Elements(ConfluenceNamespaces.Ac + "parameter")
            .FirstOrDefault(p => string.Equals(
                (string?)p.Attribute(ConfluenceNamespaces.Ac + "name"), name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static string RenderCodeMacro(XElement macro)
    {
        var language = GetMacroParameter(macro, "language");
        var body = macro.Element(ConfluenceNamespaces.Ac + "plain-text-body")?.Value
                   ?? macro.Element(ConfluenceNamespaces.Ac + "rich-text-body")?.Value
                   ?? string.Empty;
        var content = body.Trim('\r', '\n');
        var fence = FenceFor(content);
        return $"{fence}{language}\n{content}\n{fence}";
    }

    private static string RenderCalloutMacro(XElement macro, RenderState state, string directive, string originalMacroName)
    {
        if (originalMacroName == "tip")
        {
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Info,
                IssueCategory.LossyTransform,
                "Confluence's 'tip' panel has no dedicated callout in RocketWiki v1 (design.md §4 defines info/warning/note only); mapped to ':::info'.",
                state.CurrentLocation));
        }

        var title = GetMacroParameter(macro, "title");
        var bodyElement = macro.Element(ConfluenceNamespaces.Ac + "rich-text-body");
        var innerBlocks = bodyElement is null ? [] : RenderBlocks(bodyElement.Nodes(), state);

        if (!string.IsNullOrWhiteSpace(title))
        {
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Lossy,
                IssueCategory.LossyTransform,
                $"Panel title \"{title}\" has no place in the ':::{directive}' directive syntax; flattened into the callout body as bold text.",
                state.CurrentLocation));
            innerBlocks.Insert(0, $"**{MarkdownText.Escape(title)}**");
        }

        var body = JoinBlocks(innerBlocks);
        return $":::{directive}\n{body}\n:::";
    }

    private static string? RenderTocMacro(RenderState state)
    {
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Info,
            IssueCategory.DroppedElement,
            "Table of Contents macro was dropped; Markdown has no live TOC and the RocketWiki editor does not render one (design.md §13).",
            state.CurrentLocation));
        return null;
    }

    private static string RenderStatusMacro(XElement macro, RenderState state)
    {
        var title = GetMacroParameter(macro, "title") ?? "status";
        var colour = GetMacroParameter(macro, "colour");
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.LossyTransform,
            $"Status macro \"{title}\"{(colour is null ? string.Empty : $" (colour: {colour})")} has no Markdown equivalent; rendered as inline code, its colour was lost.",
            state.CurrentLocation));
        return WrapCode(title);
    }

    private static string? RenderExpandMacro(XElement macro, RenderState state)
    {
        var title = GetMacroParameter(macro, "title");
        var bodyElement = macro.Element(ConfluenceNamespaces.Ac + "rich-text-body");
        var innerBlocks = bodyElement is null ? [] : RenderBlocks(bodyElement.Nodes(), state);

        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.LossyTransform,
            $"Expand/collapsible section{(string.IsNullOrWhiteSpace(title) ? string.Empty : $" \"{title}\"")} has no collapsible equivalent in RocketWiki; flattened to always-visible content.",
            state.CurrentLocation));

        var blocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            blocks.Add($"**{MarkdownText.Escape(title)}**");
        }

        blocks.AddRange(innerBlocks);
        return blocks.Count == 0 ? null : JoinBlocks(blocks);
    }

    private static string? RenderUnsupportedMacro(XElement macro, RenderState state, string macroName)
    {
        var richBody = macro.Element(ConfluenceNamespaces.Ac + "rich-text-body");
        var plainBody = macro.Element(ConfluenceNamespaces.Ac + "plain-text-body");

        List<string> innerBlocks;
        if (richBody is not null)
        {
            innerBlocks = RenderBlocks(richBody.Nodes(), state);
        }
        else if (plainBody is not null)
        {
            var text = MarkdownText.NormalizeWhitespace(plainBody.Value);
            innerBlocks = text.Length == 0 ? [] : [MarkdownText.Escape(text)];
        }
        else
        {
            innerBlocks = [];
        }

        var hasContent = innerBlocks.Count > 0;
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.UnsupportedMacro,
            hasContent
                ? $"Macro '{macroName}' has no Markdown equivalent in this converter; its text content was preserved but the macro's specific behavior and rendering were lost. Review manually."
                : $"Macro '{macroName}' has no Markdown equivalent in this converter and had no extractable text content; it was dropped entirely. Review manually.",
            state.CurrentLocation,
            // Deliberately the bare macro name, not a formatted string like
            // "ac:name=\"foo\"" - this is machine-consumed too (ImportReportSummarizer
            // groups unsupported-macro occurrences by Detail to build the pipeline's
            // "unsupported macros" rollup), so it's kept exactly as data, not prose.
            macroName));

        return hasContent ? JoinBlocks(innerBlocks) : null;
    }

    private static string RenderAcLink(XElement element, RenderState state)
    {
        var riPage = element.Element(ConfluenceNamespaces.Ri + "page");
        var riAttachment = element.Element(ConfluenceNamespaces.Ri + "attachment");
        var riUser = element.Element(ConfluenceNamespaces.Ri + "user");

        string? displayText = null;
        var plainTextBody = element.Element(ConfluenceNamespaces.Ac + "plain-text-link-body");
        var linkBody = element.Element(ConfluenceNamespaces.Ac + "link-body");
        if (plainTextBody is not null)
        {
            displayText = MarkdownText.NormalizeWhitespace(plainTextBody.Value);
        }
        else if (linkBody is not null)
        {
            displayText = RenderInlineTrimmed(linkBody.Nodes(), state);
        }

        if (riPage is not null)
        {
            return RenderPageLink(riPage, displayText, state);
        }

        if (riAttachment is not null)
        {
            return RenderAttachmentReference(riAttachment, displayText, state, isImage: false);
        }

        if (riUser is not null)
        {
            return RenderUserMention(riUser, displayText, state);
        }

        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.DroppedElement,
            "ac:link element did not contain a recognized target (ri:page, ri:attachment, or ri:user); dropped.",
            state.CurrentLocation,
            Snippet(element)));
        return displayText is null ? string.Empty : MarkdownText.Escape(displayText);
    }

    private static string RenderPageLink(XElement riPage, string? displayText, RenderState state)
    {
        var spaceKey = (string?)riPage.Attribute(ConfluenceNamespaces.Ri + "space-key") ?? state.PageContext.SpaceKey;
        var title = (string?)riPage.Attribute(ConfluenceNamespaces.Ri + "content-title");
        var contentId = (string?)riPage.Attribute(ConfluenceNamespaces.Ri + "content-id");
        var text = string.IsNullOrEmpty(displayText) ? title ?? "link" : displayText;

        if (state.Resolver.TryResolvePage(new ConfluencePageReference(spaceKey, title, contentId), out var pageId))
        {
            return $"[{MarkdownText.Escape(text)}](page://{pageId})";
        }

        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.UnresolvedLink,
            $"Link to Confluence page \"{title ?? contentId ?? "unknown"}\" (space {spaceKey ?? "?"}) could not be resolved to a RocketWiki page id; the link was flattened to plain text. This page may not have been migrated, or was migrated out of order.",
            state.CurrentLocation));
        return MarkdownText.Escape(text);
    }

    private static string RenderUserMention(XElement riUser, string? displayText, RenderState state)
    {
        var userKey = (string?)riUser.Attribute(ConfluenceNamespaces.Ri + "userkey");
        var text = string.IsNullOrEmpty(displayText) ? "@user" : displayText;
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.UnresolvedLink,
            $"User mention (userkey {userKey ?? "?"}) was flattened to plain text. Mapping Confluence users to RocketWiki '@[display](user://{{id}})' mentions is the import pipeline's responsibility, not this converter's.",
            state.CurrentLocation));
        return MarkdownText.Escape(text);
    }

    private static string RenderAttachmentReference(XElement riAttachment, string? displayText, RenderState state, bool isImage)
    {
        var fileName = (string?)riAttachment.Attribute(ConfluenceNamespaces.Ri + "filename") ?? "attachment";
        var nestedPage = riAttachment.Element(ConfluenceNamespaces.Ri + "page");

        var spaceKey = state.PageContext.SpaceKey;
        var pageTitle = state.PageContext.PageTitle;
        var contentId = state.PageContext.ContentId;
        if (nestedPage is not null)
        {
            spaceKey = (string?)nestedPage.Attribute(ConfluenceNamespaces.Ri + "space-key") ?? spaceKey;
            pageTitle = (string?)nestedPage.Attribute(ConfluenceNamespaces.Ri + "content-title") ?? pageTitle;
            contentId = (string?)nestedPage.Attribute(ConfluenceNamespaces.Ri + "content-id") ?? contentId;
        }

        var reference = new ConfluenceAttachmentReference(spaceKey, pageTitle, contentId, fileName);
        if (state.Resolver.TryResolveAttachment(reference, out var attachmentId))
        {
            var text = string.IsNullOrEmpty(displayText) ? fileName : displayText;
            return isImage
                ? $"![{MarkdownText.Escape(text)}](attachment://{attachmentId})"
                : $"[{MarkdownText.Escape(text)}](attachment://{attachmentId})";
        }

        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.UnresolvedLink,
            isImage
                ? $"Image attachment \"{fileName}\" could not be resolved to a RocketWiki attachment id; the image was dropped. Verify the attachment was migrated."
                : $"Attachment link \"{fileName}\" could not be resolved to a RocketWiki attachment id; the link was flattened to plain text. Verify the attachment was migrated.",
            state.CurrentLocation));
        return isImage ? string.Empty : MarkdownText.Escape(string.IsNullOrEmpty(displayText) ? fileName : displayText);
    }

    private static string RenderAcImage(XElement element, RenderState state)
    {
        var riAttachment = element.Element(ConfluenceNamespaces.Ri + "attachment");
        var riUrl = element.Element(ConfluenceNamespaces.Ri + "url");
        var alt = (string?)element.Attribute(ConfluenceNamespaces.Ac + "alt");

        if (riAttachment is not null)
        {
            return RenderAttachmentReference(riAttachment, alt, state, isImage: true);
        }

        if (riUrl is not null)
        {
            var url = (string?)riUrl.Attribute(ConfluenceNamespaces.Ri + "value") ?? string.Empty;
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Info,
                IssueCategory.LossyTransform,
                "Image references an external URL (ri:url) rather than a Confluence attachment; passed through as-is.",
                state.CurrentLocation,
                url));
            return $"![{MarkdownText.Escape(alt ?? string.Empty)}]({url})";
        }

        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.DroppedElement,
            "ac:image element had no recognized source (ri:attachment or ri:url); dropped.",
            state.CurrentLocation,
            Snippet(element)));
        return string.Empty;
    }
}
