namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.15: the instance's configured selector categories, validated once at
/// startup and immutable thereafter. It is the vocabulary that the marking mutation and
/// the grant mutations validate input against, that <see cref="SelectorGate"/> consults
/// to tell an unconfigured category from an ungranted one, and that
/// <see cref="ProtectiveMarking.FormatLabel"/> takes the <i>display order</i> from.
///
/// <para><b>Built once, in the host, from configuration</b> (the API's
/// <c>ProtectiveMarkingConfiguration</c>; the sync CLI and the importer read the same
/// section). It reaches the data layer stamped into the <c>DbContextOptions</c> — the same
/// pattern as the local instance id — because it is deployment configuration exactly like
/// the instance id, and Core takes no dependency on the options framework to carry
/// it.</para>
///
/// <para><b>A category is a name, a description and its values — nothing else.</b> It used
/// to carry a claim name as well: the Keycloak attribute whose <c>yes</c> made a principal
/// eligible for the category before any grant was consulted. That went with the
/// eligibility gate (see <see cref="SelectorGate"/>), because this deployment carries no
/// per-category attributes in Keycloak; with it went the catalog's list of claim names and
/// the rule that a claim name may not be <c>groups</c>, <c>sub</c> or <c>nationality</c>,
/// since there is no longer a claim to reserve against. Whether a principal may read a
/// selector-bearing page is decided by the space's access grants alone.</para>
///
/// <para><b><see cref="Empty"/> is the fail-closed catalog.</b> An instance with no
/// configured categories knows no selector, so any page carrying one is readable by
/// nobody (<see cref="SelectorGate"/> reports <c>selector:unknown:{CATEGORY}</c>). That is
/// the §12 posture for a bundle arriving from an instance that configured a category this
/// one has not: the content lands, invisible, until an operator configures the category
/// and an admin grants it. There is no "unknown means ignore" reading anywhere.</para>
///
/// <para><b>Validation happens here and nowhere else</b>, and it is what makes the
/// category name safe to put into a denial reason: every name and value is canonical
/// upper-case from the closed charset <c>[A-Z0-9_-]</c>, at most
/// <see cref="MaxNameLength"/>/<see cref="MaxValueLength"/> long (the column lengths of
/// <c>PageMarkingSelectors</c> and <c>AccessRuleSelectors</c> — SQLite does not enforce
/// declared lengths, so the catalog is what keeps an over-long token out of the test tier),
/// and unique. Invalid configuration throws <see cref="SelectorCatalogException"/>, which
/// the host turns into a refusal to start: a wiki that silently dropped a mis-typed
/// category would be enforcing a vocabulary its operator did not configure.</para>
/// </summary>
public sealed class SelectorCatalog
{
    /// <summary>Column length of <c>PageMarkingSelectors.Category</c> / <c>AccessRuleSelectors.Category</c>.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Column length of <c>PageMarkingSelectors.Value</c> / <c>AccessRuleSelectors.Value</c>.</summary>
    public const int MaxValueLength = 32;

    private readonly Dictionary<string, SelectorCategory> _byName;
    private readonly Dictionary<string, int> _displayIndexByName;

    private SelectorCatalog(IReadOnlyList<SelectorCategory> categories)
    {
        Categories = categories;
        _byName = categories.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _displayIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < categories.Count; i++)
        {
            _displayIndexByName[categories[i].Name] = i;
        }
    }

    /// <summary>No categories: every selector is unknown, every selector-bearing page is
    /// readable by nobody. See the class doc for why that is the right default.</summary>
    public static SelectorCatalog Empty { get; } = new([]);

    /// <summary>The categories in <b>configured</b> order, canonical. Configured order is
    /// display order (§21.15): an operator who lists <c>FRUIT</c> before <c>REGION</c> gets
    /// <c>APPLE NORTH</c>, not <c>NORTH APPLE</c>.</summary>
    public IReadOnlyList<SelectorCategory> Categories { get; }

    public int Count => Categories.Count;

    /// <summary>Looks a category up by canonical name. Ordinal: the name must already be
    /// canonical, which every <see cref="SelectorValue"/> guarantees; a non-canonical
    /// string is simply unknown, which is the fail-closed answer.</summary>
    public bool TryGet(string categoryName, out SelectorCategory category) =>
        _byName.TryGetValue(categoryName, out category!);

    /// <summary>True iff the category is configured AND the value is one of its values.</summary>
    public bool IsKnown(SelectorValue selector) =>
        _byName.TryGetValue(selector.Category, out var category)
        && category.Values.Contains(selector.Value, StringComparer.Ordinal);

    /// <summary>The configured position of a category, or <see cref="int.MaxValue"/> for
    /// one this instance does not know — so unknown categories sort last in a label,
    /// after every configured one, and still render (§21.15: a marking must read back as
    /// the thing that is enforced, and an unknown selector IS enforced, against everyone).</summary>
    public int DisplayIndex(string categoryName) =>
        _displayIndexByName.TryGetValue(categoryName, out var index) ? index : int.MaxValue;

    /// <summary>
    /// The closed token grammar every category name and value must satisfy once
    /// canonical: <c>[A-Z0-9_-]</c>, non-empty, at most <paramref name="maxLength"/>. No
    /// whitespace (a label is space-separated) and no <c>/</c> (the caveat's own
    /// separator). Public so the sync importer applies the same grammar to a token that
    /// arrived in a bundle before it reaches a column.
    /// </summary>
    public static bool IsWellFormedToken(string canonicalToken, int maxLength = MaxValueLength)
    {
        if (string.IsNullOrEmpty(canonicalToken) || canonicalToken.Length > maxLength)
        {
            return false;
        }

        foreach (var c in canonicalToken)
        {
            if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds a catalog, throwing <see cref="SelectorCatalogException"/> on the
    /// first invalid definition. The host calls this at startup so an invalid
    /// configuration fails the process rather than the first request.</summary>
    public static SelectorCatalog Create(IEnumerable<SelectorCategory> definitions) =>
        TryCreate(definitions, out var catalog, out var error)
            ? catalog
            : throw new SelectorCatalogException(error!);

    /// <summary>
    /// Non-throwing twin of <see cref="Create"/>. Every failure names the category it was
    /// found in, so the message is actionable from a startup log line. Names and values
    /// are canonicalized (trimmed, upper-cased) before the grammar is applied — an
    /// operator may write <c>fruit</c>; the catalog says <c>FRUIT</c>.
    /// </summary>
    public static bool TryCreate(
        IEnumerable<SelectorCategory> definitions, out SelectorCatalog catalog, out string? error)
    {
        var categories = new List<SelectorCategory>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            var name = SelectorValue.Canonicalize(definition.Name);
            if (!IsWellFormedToken(name, MaxNameLength))
            {
                error = $"Selector category '{definition.Name}' has an invalid name: a category name must be " +
                    $"1-{MaxNameLength} characters from [A-Z0-9_-] (letters are upper-cased).";
                catalog = Empty;
                return false;
            }

            if (!seenNames.Add(name))
            {
                error = $"Selector category '{name}' is configured more than once.";
                catalog = Empty;
                return false;
            }

            var values = new List<string>();
            var seenValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawValue in definition.Values ?? [])
            {
                var value = SelectorValue.Canonicalize(rawValue);
                if (!IsWellFormedToken(value, MaxValueLength))
                {
                    error = $"Selector category '{name}' has an invalid value '{rawValue}': a value must be " +
                        $"1-{MaxValueLength} characters from [A-Z0-9_-] (letters are upper-cased).";
                    catalog = Empty;
                    return false;
                }

                if (!seenValues.Add(value))
                {
                    error = $"Selector category '{name}' lists the value '{value}' more than once.";
                    catalog = Empty;
                    return false;
                }

                values.Add(value);
            }

            if (values.Count == 0)
            {
                error = $"Selector category '{name}' declares no values; a category with nothing to pick from " +
                    "cannot appear on any marking.";
                catalog = Empty;
                return false;
            }

            categories.Add(new SelectorCategory(name, definition.Description, values));
        }

        catalog = new SelectorCatalog(categories);
        error = null;
        return true;
    }
}

/// <summary>Thrown by <see cref="SelectorCatalog.Create"/> for an invalid definition. The
/// host lets it propagate out of startup (design.md §15's fail-closed configuration
/// family): a wiki must not run on a vocabulary its operator did not configure.</summary>
public sealed class SelectorCatalogException(string message) : Exception(message);
