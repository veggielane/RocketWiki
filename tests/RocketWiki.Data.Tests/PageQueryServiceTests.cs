using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// <see cref="PageQueryService"/> at the data tier (design.md §22): the properties that are
/// about generated SQL and about the clock, both of which are invisible from the API tier.
///
/// <para>Deliberately does not derive from <see cref="SqliteTestBase"/>: the parameterization
/// test needs a <see cref="DbCommandInterceptor"/> on the context, which means owning the
/// options builder.</para>
/// </summary>
public sealed class PageQueryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CapturingInterceptor _commands = new();

    public PageQueryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private RocketWikiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlite(_connection)
            .UseLocalInstanceId("local-instance")
            .AddInterceptors(_commands)
            .Options);

    private static Principal Anyone(string subject = "sub-caller") => Principal.Create(subject, []);

    private async Task<Space> SeedSpaceAsync(string key = "ENG", string? expressionJson = null)
    {
        await using var db = CreateContext();
        var user = TestData.NewUser();
        db.Users.Add(user);

        var space = TestData.NewSpace(key);
        space.CreatedByUserId = user.Id;
        db.Spaces.Add(space);

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = expressionJson ?? RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = user.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = user.Id,
        });

        await db.SaveChangesAsync();
        return space;
    }

    private async Task<Page> SeedPageAsync(Space space, string slug, string title, DateTime? updatedAtUtc = null)
    {
        await using var db = CreateContext();
        var page = TestData.NewPage(space, slug);
        page.Title = title;
        if (updatedAtUtc is not null)
        {
            page.UpdatedAtUtc = updatedAtUtc.Value;
        }

        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private async Task<IReadOnlyList<Guid>> RunAsync(string query, TimeProvider? time = null)
    {
        await using var db = CreateContext();
        var service = new PageQueryService(db, time);
        var outcome = await service.ExecuteAsync(query, Anyone(), 100);
        Assert.Empty(outcome.Errors);
        return outcome.PageIds;
    }

    [Fact]
    public async Task UserTextIsPassedAsParameters_NeverConcatenatedIntoSql()
    {
        // design.md §22: parse -> validate -> compile to a PARAMETERIZED query. The value
        // below is chosen so that if it ever reached the SQL text, the statement would
        // either error or do something interesting - the two ways this failing would show.
        const string adversarial = "Robert'); DROP TABLE Pages;--%_[x]";

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "adversarial", adversarial);
        await SeedPageAsync(space, "ordinary", "Ordinary");

        _commands.Clear();
        var matched = await RunAsync($"""space = {space.Key} AND title = "{Escape(adversarial)}" """);

        // It matched the page, so the value really did travel end to end...
        Assert.Single(matched);

        // ...and it travelled as a parameter: no fragment of it appears in any command text.
        Assert.NotEmpty(_commands.CommandTexts);
        foreach (var sql in _commands.CommandTexts)
        {
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Robert", sql, StringComparison.Ordinal);
        }

        Assert.Contains(_commands.ParameterValues, v => v is string s && s.Contains("Robert", StringComparison.Ordinal));

        // The table is still there - the belt to the braces above.
        await using var db = CreateContext();
        Assert.Equal(2, await db.Pages.CountAsync());
    }

    [Fact]
    public async Task LikePatternsAndDatesAreParametersToo()
    {
        var space = await SeedSpaceAsync("PAR");
        await SeedPageAsync(space, "p", "Percent 50% page");

        _commands.Clear();
        var matched = await RunAsync($"""space = {space.Key} AND title ~ "50%" AND created > "2000-01-01" """);
        Assert.Single(matched);

        foreach (var sql in _commands.CommandTexts)
        {
            Assert.DoesNotContain("%50", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("2000-01-01", sql, StringComparison.Ordinal);
        }

        Assert.Contains(_commands.ParameterValues, v => v is string s && s == "%50\\%%");
    }

    [Fact]
    public async Task NowIsResolvedOnceFromTheInjectedClock()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var space = await SeedSpaceAsync("CLK");
        var recent = await SeedPageAsync(space, "recent", "Recent", time.GetUtcNow().UtcDateTime.AddHours(-2));
        await SeedPageAsync(space, "stale", "Stale", time.GetUtcNow().UtcDateTime.AddDays(-30));

        // A window built from two now()s. If each resolved from its own clock read, this
        // would be a different (and potentially empty) window every run.
        var matched = await RunAsync(
            $"""space = {space.Key} AND updated > now("-1d") AND updated <= now("+1h") """, time);

        Assert.Equal([recent.Id], matched);
    }

    [Fact]
    public async Task ASpaceTheCallerHoldsNoRoleIn_IsIndistinguishableFromOneThatDoesNotExist()
    {
        // design.md §6.7, at the service seam rather than the HTTP one: identical empty
        // results, and no error to tell the two apart.
        var hidden = await SeedSpaceAsync(
            "HID", RuleExpressionSerializer.Serialize(new GroupCondition("insiders")));
        await SeedPageAsync(hidden, "secret", "Hidden Page");

        await using var db = CreateContext();
        var service = new PageQueryService(db);

        var invisible = await service.ExecuteAsync($"space = {hidden.Key}", Anyone(), 100);
        var nonexistent = await service.ExecuteAsync("space = NOSUCHKEY", Anyone(), 100);

        Assert.Empty(invisible.PageIds);
        Assert.Empty(invisible.Errors);
        Assert.Empty(nonexistent.PageIds);
        Assert.Empty(nonexistent.Errors);

        // ...and a caller who does hold the grant sees it, so the emptiness was the rule
        // engine rather than a broken query.
        var insider = await service.ExecuteAsync(
            $"space = {hidden.Key}", Principal.Create("sub-insider", ["insiders"]), 100);
        Assert.Single(insider.PageIds);
    }

    [Fact]
    public async Task ParseFailuresReturnErrorsAndRunNoQuery()
    {
        await using var db = CreateContext();
        var service = new PageQueryService(db);

        _commands.Clear();
        var outcome = await service.ExecuteAsync("""marking = "SECRET" """, Anyone(), 100);

        Assert.False(outcome.IsValid);
        Assert.Empty(outcome.PageIds);
        Assert.Empty(_commands.CommandTexts);
    }

    /// <summary>Escapes a value for embedding in an RQL double-quoted string.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>A clock that never moves — hand-rolled rather than pulling
    /// Microsoft.Extensions.TimeProvider.Testing into this project for one fixed instant.
    /// Every call returning the same value is exactly the point: it makes "resolved once"
    /// provable rather than probable.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];
        private readonly List<object?> _values = [];

        public IReadOnlyList<string> CommandTexts
        {
            get { lock (_texts) { return [.. _texts]; } }
        }

        public IReadOnlyList<object?> ParameterValues
        {
            get { lock (_texts) { return [.. _values]; } }
        }

        public void Clear()
        {
            lock (_texts)
            {
                _texts.Clear();
                _values.Clear();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            lock (_texts)
            {
                _texts.Add(command.CommandText);
                foreach (DbParameter parameter in command.Parameters)
                {
                    _values.Add(parameter.Value);
                }
            }
        }
    }
}
