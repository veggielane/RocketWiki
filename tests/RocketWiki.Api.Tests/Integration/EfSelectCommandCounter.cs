using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Data;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Counts EF Core relational SELECT commands, so the DataLoader batching tests can
/// assert "no per-row queries" as an observed fact instead of trusting the loader
/// wiring (design.md §8's DataLoader rule). Subscribes to EF's always-on
/// DiagnosticSource ("Microsoft.EntityFrameworkCore" / CommandExecuting) — no
/// interceptor registration in the factory needed — and filters twice to stay
/// deterministic under xUnit's class-level parallelism:
/// - to THIS factory's database only (each <see cref="RocketWikiApiFactory"/> owns a
///   unique temp-file SQLite database, so a concurrent test class's traffic never
///   lands in the count), and
/// - to SELECT texts matching the caller's predicate (so JIT-provisioning INSERTs,
///   audit-row INSERTs etc. don't muddy a "how many reads of table X" assertion).
/// </summary>
public sealed class EfSelectCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _dataSourceFragment;
    private readonly Func<string, bool> _selectTextFilter;
    private readonly List<string> _matched = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _allListenersSubscription;

    public EfSelectCommandCounter(RocketWikiApiFactory factory, Func<string, bool> selectTextFilter)
    {
        using var scope = factory.Services.CreateScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>()
            .Database.GetDbConnection().ConnectionString;
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        _dataSourceFragment = builder.DataSource;
        _selectTextFilter = selectTextFilter;
        _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public IReadOnlyList<string> MatchedCommands
    {
        get
        {
            lock (_matched)
            {
                return _matched.ToList();
            }
        }
    }

    public void Reset()
    {
        lock (_matched)
        {
            _matched.Clear();
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
        {
            lock (_subscriptions)
            {
                _subscriptions.Add(listener.Subscribe(this));
            }
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != RelationalEventId.CommandExecuting.Name || value.Value is not CommandEventData data)
        {
            return;
        }

        var connection = data.Command.Connection?.ConnectionString;
        if (connection is null || !connection.Contains(_dataSourceFragment, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var text = data.Command.CommandText;
        if (!text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || !_selectTextFilter(text))
        {
            return;
        }

        lock (_matched)
        {
            _matched.Add(text);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }

    public void Dispose()
    {
        _allListenersSubscription.Dispose();
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }
    }
}
