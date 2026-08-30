using System.Reflection;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Data;
using Xunit;

/// <summary>
/// design.md §12: the sync outbox is the only path content takes from low to high, and its
/// classifier's default is <c>null</c> — journal nothing.
///
/// <para>Its sibling consumer on the same event stream,
/// <c>DomainEventAuditMapper.Describe</c>, defaults the other way and throws. Opposite
/// defaults are what make this worth a test: a new content-bearing event added tomorrow
/// gets audited (loudly, if anyone forgets) but silently never syncs, and the high side
/// has no way to notice content that never arrived. Every other exhaustiveness rule in
/// this repo is swept — AuditCoverageTests over four channels, BinaryRouteErrorMapTests
/// over every PageMutationError — and this switch was the one that mattered most and was
/// swept least.</para>
///
/// <para>Types come from reflection, not a list, so adding an event to Core is what
/// breaks this test. The only thing written down by hand is the DECISION, which has to
/// live somewhere by definition — and it lives in production next to the switch that
/// depends on it, not here.</para>
/// </summary>
public class SyncOutboxWriterCoverageTests
{
    /// <summary>
    /// Journalling depends on the rule's Kind rather than the event type, so this one is
    /// asserted by name below instead of by the sweep.
    /// </summary>
    private static readonly HashSet<Type> AssertedIndividually = [typeof(AccessRuleChangedEvent)];

    private static IEnumerable<Type> AllDomainEventTypes() =>
        typeof(IDomainEvent).Assembly.GetTypes()
            .Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void EveryDomainEvent_IsEitherJournalled_OrExplicitlyDeclaredLocal()
    {
        var undecided = new List<string>();

        foreach (var type in AllDomainEventTypes())
        {
            if (AssertedIndividually.Contains(type) || SyncOutboxWriter.DeliberatelyNotJournalled.ContainsKey(type))
            {
                continue;
            }

            var instance = (IDomainEvent)Construct(type);
            if (SyncOutboxWriter.ClassifyCore(instance) is null)
            {
                undecided.Add(type.Name);
            }
        }

        Assert.True(undecided.Count == 0,
            "These domain events are journalled by nobody and declared local by nobody, so they would be dropped from " +
            "the sync outbox in silence (design.md §12). Add a case to SyncOutboxWriter.ClassifyCore, or add the type to " +
            "SyncOutboxWriter.DeliberatelyNotJournalled with the reason:\n  " + string.Join("\n  ", undecided));
    }

    [Fact]
    public void TheSweep_ActuallySeesTheEventTypes()
    {
        // Guards the sweep above against passing because it found nothing — a reflection
        // query that silently matches zero types is a green test that checks nothing.
        var all = AllDomainEventTypes().ToList();
        Assert.True(all.Count > 30, $"Expected the Core assembly to yield the full domain-event set; found {all.Count}.");
        Assert.Contains(typeof(PageCreatedEvent), all);
        Assert.Contains(typeof(LabelCreatedEvent), all);
    }

    [Fact]
    public void DeliberatelyNotJournalled_NamesOnlyRealDomainEvents_EachWithAReason()
    {
        // Keeps the exemption list from outliving what it exempts: a renamed or deleted
        // event would otherwise leave a stale entry silently excusing nothing.
        var all = AllDomainEventTypes().ToHashSet();

        foreach (var (type, reason) in SyncOutboxWriter.DeliberatelyNotJournalled)
        {
            Assert.Contains(type, all);
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{type.Name} is exempted with no reason recorded.");
        }
    }

    [Fact]
    public void AccessRuleChanged_JournalsPageRestrictionsOnly()
    {
        // §12's table: page restrictions travel with content; space grants stay local,
        // because the high side decides who may read its own replica.
        var restriction = new AccessRuleChangedEvent(
            Guid.NewGuid(), "ENG", Guid.NewGuid(), null, SnapshotOfKind(AccessRuleKind.PageRestriction));
        var grant = new AccessRuleChangedEvent(
            Guid.NewGuid(), "ENG", Guid.NewGuid(), null, SnapshotOfKind(AccessRuleKind.SpaceGrant));

        Assert.Equal(SyncEventType.Restrictions, SyncOutboxWriter.ClassifyCore(restriction));
        Assert.Null(SyncOutboxWriter.ClassifyCore(grant));
    }

    private static AccessRuleSnapshot SnapshotOfKind(AccessRuleKind kind)
    {
        var snapshot = (AccessRuleSnapshot)Construct(typeof(AccessRuleSnapshot));
        return snapshot with { Kind = kind };
    }

    /// <summary>
    /// Builds an event from its primary constructor with placeholder arguments. The
    /// classifier switches on type (and, for one case, on a property asserted separately),
    /// so the values are irrelevant — what matters is getting one instance of every type
    /// without naming them here, which is the point of the sweep.
    /// </summary>
    private static object Construct(Type type)
    {
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters().Select(p => Placeholder(p.ParameterType)).ToArray();
        return ctor.Invoke(args);
    }

    private static object? Placeholder(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return null;
        }

        if (type == typeof(string))
        {
            return "placeholder";
        }

        if (type == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (type == typeof(DateTime))
        {
            return DateTime.UtcNow;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).GetValue(0);
        }

        if (type.IsValueType)
        {
            return Activator.CreateInstance(type);
        }

        if (type.IsArray)
        {
            return Array.CreateInstance(type.GetElementType()!, 0);
        }

        // Reference types (nested records, collections) are left null: no classifier case
        // dereferences one except AccessRuleChangedEvent's, which is asserted by name.
        return null;
    }
}
