using System.Globalization;

namespace RocketWiki.Core.Query;

/// <summary>
/// The closed field vocabulary and the operators each field accepts (design.md §22), plus
/// the two deliberately-named refusal categories that sit outside it. One place, so the
/// validator, the printer, and every error message agree on spelling.
///
/// <para><b>Three different "no"s, on purpose.</b> An author who writes something RQL will
/// not answer deserves to know <i>which</i> kind of no they got:</para>
/// <list type="number">
/// <item><b>Not queryable</b> (<see cref="NotQueryableFields"/>) — classification,
/// clearance, and permission state. Refused by name with a message that teaches the rule.
/// Silently ignoring these would be worse (the author would believe the filter applied),
/// and reporting them as unknown fields would be a lie that invites a retry with a
/// different spelling. Why they are excluded at all is design.md §21.8's reasoning applied
/// to a query box: a filter over classification is a census of the classified estate, and
/// running it needs no read access to a single page it counts.</item>
/// <item><b>Not supported yet</b> (<see cref="NotYetSupportedFields"/>) — today just
/// <c>text</c>. A real field, deliberately absent from v1, with an honest reason.</item>
/// <item><b>Unknown</b> — anything else. A typo, answered with the allowed list.</item>
/// </list>
///
/// <para>Field names match case-insensitively (keywords do too), and matching happens on
/// the ordinal-lowercased name, so <c>Marking</c>, <c>MARKING</c> and <c>marking</c> all
/// land in the same bucket. This is <i>not</i> the exact-ordinal matching design.md §6.3
/// mandates for rule evaluation — no access decision is made here; being case-forgiving
/// about a field <i>name</i> costs nothing and being case-forgiving about a group name
/// would cost everything.</para>
/// </summary>
public static class RqlVocabulary
{
    private static readonly Dictionary<string, RqlField> FieldsByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["label"] = RqlField.Label,
        ["space"] = RqlField.Space,
        ["title"] = RqlField.Title,
        ["created"] = RqlField.Created,
        ["updated"] = RqlField.Updated,
        ["creator"] = RqlField.Creator,
    };

    /// <summary>
    /// Names that name classification, caveat, clearance, or permission state. Refused with
    /// <see cref="RqlErrorCode.NotQueryableField"/>. The list is generous on purpose —
    /// plurals and underscore spellings included — because every name in it is a name whose
    /// author is reaching for the forbidden concept, and "unknown field" would send them
    /// looking for the right spelling of a thing that must not exist.
    /// </summary>
    private static readonly HashSet<string> NotQueryableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "marking", "markings",
        "classification", "classifications",
        "level", "levels",
        "eyesonly", "eyes_only", "eyes-only",
        "caveat", "caveats",
        "prefix", "prefixes",
        "restricted", "restriction", "restrictions",
        "permission", "permissions",
        "clearance", "clearances",
        "group", "groups",
        "nationality", "nationalities",
    };

    /// <summary>Real fields deliberately not in v1, each with its own reason (see <see cref="DescribeUnsupported"/>).</summary>
    private static readonly HashSet<string> NotYetSupportedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
    };

    private static readonly Dictionary<RqlField, RqlOperator[]> OperatorsByField = new()
    {
        [RqlField.Label] =
        [
            RqlOperator.Equals, RqlOperator.NotEquals, RqlOperator.In, RqlOperator.NotIn,
            RqlOperator.IsEmpty, RqlOperator.IsNotEmpty,
        ],
        [RqlField.Space] = [RqlOperator.Equals, RqlOperator.NotEquals, RqlOperator.In, RqlOperator.NotIn],
        [RqlField.Title] = [RqlOperator.Equals, RqlOperator.NotEquals, RqlOperator.Contains, RqlOperator.NotContains],
        [RqlField.Created] =
        [
            RqlOperator.Equals, RqlOperator.NotEquals, RqlOperator.GreaterThan,
            RqlOperator.GreaterThanOrEqual, RqlOperator.LessThan, RqlOperator.LessThanOrEqual,
        ],
        [RqlField.Updated] =
        [
            RqlOperator.Equals, RqlOperator.NotEquals, RqlOperator.GreaterThan,
            RqlOperator.GreaterThanOrEqual, RqlOperator.LessThan, RqlOperator.LessThanOrEqual,
        ],
        [RqlField.Creator] = [RqlOperator.Equals, RqlOperator.NotEquals],
    };

    /// <summary>The fields ORDER BY accepts (design.md §22). Deliberately narrower than the
    /// filterable set: sorting by label or space would need a join whose ordering is not
    /// well-defined for a page carrying several labels.</summary>
    private static readonly RqlField[] SortableFieldsInternal = [RqlField.Created, RqlField.Title, RqlField.Updated];

    /// <summary>The allowed field names, alphabetical — as they appear in every error message.</summary>
    public static string QueryableFieldList { get; } =
        string.Join(", ", FieldsByName.Keys.OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>The ORDER BY field names, alphabetical.</summary>
    public static string SortableFieldList { get; } =
        string.Join(", ", SortableFieldsInternal.Select(NameOf).OrderBy(n => n, StringComparer.Ordinal));

    public static IReadOnlyList<RqlField> SortableFields => SortableFieldsInternal;

    public static bool IsSortable(RqlField field) => SortableFieldsInternal.Contains(field);

    public static bool TryResolveField(string name, out RqlField field) => FieldsByName.TryGetValue(name, out field);

    public static bool IsNotQueryable(string name) => NotQueryableFields.Contains(name);

    public static bool IsNotYetSupported(string name) => NotYetSupportedFields.Contains(name);

    public static IReadOnlyList<RqlOperator> OperatorsFor(RqlField field) => OperatorsByField[field];

    public static bool Accepts(RqlField field, RqlOperator op) => OperatorsByField[field].Contains(op);

    public static string NameOf(RqlField field) => field switch
    {
        RqlField.Label => "label",
        RqlField.Space => "space",
        RqlField.Title => "title",
        RqlField.Created => "created",
        RqlField.Updated => "updated",
        RqlField.Creator => "creator",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unhandled RQL field."),
    };

    public static string NameOf(RqlOperator op) => op switch
    {
        RqlOperator.Equals => "=",
        RqlOperator.NotEquals => "!=",
        RqlOperator.Contains => "~",
        RqlOperator.NotContains => "!~",
        RqlOperator.GreaterThan => ">",
        RqlOperator.GreaterThanOrEqual => ">=",
        RqlOperator.LessThan => "<",
        RqlOperator.LessThanOrEqual => "<=",
        RqlOperator.In => "IN",
        RqlOperator.NotIn => "NOT IN",
        RqlOperator.IsEmpty => "IS EMPTY",
        RqlOperator.IsNotEmpty => "IS NOT EMPTY",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unhandled RQL operator."),
    };

    /// <summary>Whether the field holds a date, which decides whether <c>now()</c> and date literals belong on it.</summary>
    public static bool IsDateField(RqlField field) => field is RqlField.Created or RqlField.Updated;

    /// <summary>
    /// The message for a classification/permission field. Names the field, says
    /// <i>not queryable</i> rather than unknown, and gives the reason — an author who is
    /// told only "no" will try a synonym.
    /// </summary>
    public static string DescribeNotQueryable(string name) => string.Format(
        CultureInfo.InvariantCulture,
        "Field '{0}' is not queryable. Classification, caveat, clearance and permission state are "
        + "deliberately excluded from RQL: a filter over them would report on classified content the "
        + "caller cannot read (design.md §21.8). Queryable fields are: {1}.",
        name,
        QueryableFieldList);

    /// <summary>The message for a real-but-unshipped field. Distinct from "unknown" so the reason is honest.</summary>
    public static string DescribeUnsupported(string name) => string.Format(
        CultureInfo.InvariantCulture,
        "Field '{0}' is not supported yet. A text predicate has to compile onto the ranked hybrid "
        + "full-text/vector path, which ranks rather than filters, so it would not mean the same thing "
        + "here as it does on the search page; use the `search` field for full-text queries. Queryable "
        + "fields are: {1}.",
        name,
        QueryableFieldList);

    /// <summary>The message for a name that is simply not a field.</summary>
    public static string DescribeUnknown(string name) => string.Format(
        CultureInfo.InvariantCulture,
        "Unknown field '{0}'. Queryable fields are: {1}.",
        name,
        QueryableFieldList);

    public static string DescribeOperatorNotAllowed(RqlField field, RqlOperator op) => string.Format(
        CultureInfo.InvariantCulture,
        "Operator '{0}' is not allowed for field '{1}'. Allowed operators for '{1}' are: {2}.",
        NameOf(op),
        NameOf(field),
        string.Join(", ", OperatorsFor(field).Select(NameOf)));
}
