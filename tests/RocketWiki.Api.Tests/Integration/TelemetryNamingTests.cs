using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.RegularExpressions;
using RocketWiki.Core.Telemetry;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §15's naming rule, enforced rather than reviewed: instruments are
/// <c>rocketwiki.&lt;area&gt;.&lt;thing&gt;</c> and tags <c>rocketwiki.&lt;area&gt;.&lt;tag&gt;</c>,
/// OpenTelemetry's lowercase dotted convention. <see cref="TelemetryRegistrationTests"/>
/// covers the neighbouring rule (source and meter names must match the
/// <c>RocketWiki.*</c> wildcard ServiceDefaults subscribes with); this covers what those
/// sources and meters then emit.
///
/// It exists because the drift is invisible at runtime: an area-less tag key
/// (<c>rocketwiki.outcome</c> — which is what DataTelemetry.OutcomeTag actually was)
/// compiles, exports, and simply lands in a different place on a dashboard than every
/// sibling. Reflection over the telemetry classes is the only way to see the whole set
/// at once, and it means a new instrument or tag is covered the day it is added rather
/// than the day someone remembers to extend a list.
///
/// RocketWiki.Importer is asserted the same way in its own test project
/// (<c>ImporterTelemetryTests</c>) — it is deliberately outside the API's reference
/// graph, the same split TelemetryRegistrationTests documents for source names.
/// </summary>
public class TelemetryNamingTests
{
    /// <summary>
    /// <c>rocketwiki.</c>, then an area segment, then at least one more segment. Dots are
    /// allowed after the area so a qualified instrument
    /// (<c>rocketwiki.mcp.tool_call.duration</c>) passes; uppercase, dashes and spaces
    /// never do.
    /// </summary>
    public const string NamePattern = @"^rocketwiki\.[a-z0-9_]+\.[a-z0-9_.]+$";

    private static readonly Regex Name = new(NamePattern, RegexOptions.Compiled);

    private static readonly Type[] TelemetryClasses =
    [
        typeof(RocketWiki.Api.Telemetry.ApiTelemetry),
        typeof(CoreTelemetry),
        typeof(RocketWiki.Data.Telemetry.DataTelemetry),
        typeof(RocketWiki.Storage.StorageTelemetry),
    ];

    [Fact]
    public void EveryInstrumentAndTagKeyFollowsTheSectionFifteenNamingConvention()
    {
        var violations = new List<string>();
        var instrumentNames = new List<string>();
        var tagKeys = new List<string>();

        foreach (var telemetryClass in TelemetryClasses)
        {
            foreach (var field in telemetryClass.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (typeof(Instrument).IsAssignableFrom(field.FieldType))
                {
                    var instrument = (Instrument?)field.GetValue(null);
                    Assert.NotNull(instrument);
                    instrumentNames.Add(instrument.Name);

                    if (!Name.IsMatch(instrument.Name))
                    {
                        violations.Add(
                            $"instrument {telemetryClass.Name}.{field.Name} is named '{instrument.Name}'");
                    }

                    continue;
                }

                // Tag KEYS by convention: a const string whose name ends in "Tag". Tag
                // VALUES (outcome vocabularies, span names) are deliberately out of scope
                // - they are not dotted keys and §15 bounds them a different way.
                if (field is { IsLiteral: true, IsInitOnly: false }
                    && field.FieldType == typeof(string)
                    && field.Name.EndsWith("Tag", StringComparison.Ordinal))
                {
                    var key = (string?)field.GetRawConstantValue();
                    Assert.NotNull(key);
                    tagKeys.Add(key);

                    if (!Name.IsMatch(key))
                    {
                        violations.Add($"tag {telemetryClass.Name}.{field.Name} is keyed '{key}'");
                    }
                }
            }
        }

        // Non-vacuous: if reflection stops finding instruments or tag constants (a class
        // renamed, fields made non-public), this guard must fail rather than pass over an
        // empty set.
        Assert.NotEmpty(instrumentNames);
        Assert.NotEmpty(tagKeys);

        // Every violation in one message: fixing them one failed build at a time is how a
        // convention rots.
        Assert.True(violations.Count == 0,
            $"design.md §15: instruments must be named 'rocketwiki.<area>.<thing>' and tags "
            + $"'rocketwiki.<area>.<tag>' (regex {NamePattern}). Offenders:\n"
            + string.Join('\n', violations.Select(v => "  - " + v)));
    }
}
