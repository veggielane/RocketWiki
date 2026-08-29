namespace RocketWiki.Core.Forms;

/// <summary>What a field accepts. Deliberately four, and deliberately not extensible by
/// configuration: every member here is a validation rule, an input control and a sort
/// order, and a type nobody has asked for is three pieces of code with no user.</summary>
public enum FormFieldType
{
    Text,
    Number,
    Date,
    Select,
}

/// <summary>
/// One field of a form. <paramref name="Options"/> is empty for every type but
/// <see cref="FormFieldType.Select"/>, where it is the allowed values in the order the
/// author wrote them — that order is the picker's order, so it is content, not a set.
/// </summary>
public sealed record FormField(
    string Name,
    FormFieldType Type,
    bool Required,
    IReadOnlyList<string> Options);

/// <summary>
/// A form, as declared by a ```` ```form-definition ```` fence on a page
/// (docs/ENTRIES-AND-FORMS-PLAN.md).
///
/// <para>The definition lives in page content rather than a table on purpose: it then
/// round-trips as text, is versioned by the page's own revision history, is diffable and
/// restorable for free, and needs no schema migration when the field vocabulary grows.
/// The cost is that the server cannot query it without parsing content, which is why
/// this parser is here in Core — validation and rendering must agree, and the way to
/// guarantee that is one grammar with the server as its authority (the same rule §22's
/// RQL follows, where the SPA deliberately does not re-parse).</para>
/// </summary>
public sealed record FormDefinition(string Collection, IReadOnlyList<FormField> Fields);

/// <summary>A definition that could not be parsed, kept rather than discarded so the
/// author is told which line and why instead of watching their form silently vanish.</summary>
public sealed record FormDefinitionError(string Collection, string Message);
